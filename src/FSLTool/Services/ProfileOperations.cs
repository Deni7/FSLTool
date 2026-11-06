using System.Diagnostics;
using System.Management;
using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>The container no longer qualifies for the operation — it is skipped, not failed.</summary>
public sealed class OperationSkippedException(string message) : InvalidOperationException(message);

/// <summary>High-level operations on a single container. All methods are blocking — call from a worker thread.</summary>
public static class ProfileOperations
{
    /// <summary>Throws if anybody uses the container right now.</summary>
    /// <summary>
    /// Throws if anybody uses the container right now. Cheap checks first, so batches stay fast:
    ///  1. open the file for shared reading — fails if anyone (SMB client, local attach, other process) holds it for writing;
    ///  2. SMB opens of exactly this file via NetFileEnum filtered by path (catches read-only opens; no WMI, no DNS).
    /// The full WMI query (all open files of the server + reverse DNS) is made only if NetFileEnum is unavailable;
    /// earlier it ran on EVERY call, which made batches over hundreds of containers slow.
    /// </summary>
    public static void EnsureFree(ProfileInfo p)
    {
        var local = PathMapper.ToLocal(p.FilePath);
        bool isLocal = !local.StartsWith(@"\\");
        var st = ProfileScanner.ProbeLock(p.FilePath, isLocal);
        // containers on another server: their SMB opens are not visible here — the lock probe decides
        var fast = isLocal ? SmbService.FindOpens(local) : new List<SmbHandle>();

        if (fast == null)
        {
            // NetFileEnum not available → the old, complete query
            var smb = SmbService.GetOpenFiles().Where(h => h.LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase)).ToList();
            if (smb.Count > 0) { p.SetHandles(smb); fast = smb; }
            else fast = new List<SmbHandle>();
        }
        if (fast.Count > 0)
        {
            // keep the host names the scan already found (NetFileEnum knows only the user)
            if (p.Handles.Count == 0 || p.State != MountState.InUseSmb) p.SetHandles(fast);
            p.State = MountState.InUseSmb;
            var who = string.Join(", ", p.Handles.Select(h => (h.ClientComputer + " " + h.ClientUser).Trim()).Distinct());
            throw new InvalidOperationException($"Контейнер використовується ({who}).");
        }
        if (st == MountState.Free)
        {
            p.State = MountState.Free;
            return;
        }
        p.State = st;
        throw new InvalidOperationException("Контейнер зайнятий (" + p.StateText + ").");
    }

    /// <summary>Read-only attach: real used/free space, largest folders, and what the rules would free.</summary>
    public static void Analyze(ProfileInfo p, CleanupRuleSet rules, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.02);
        EnsureFree(p);
        r.Stage("Підключення диска (лише читання)", 0.02, 0.06);
        using var disk = AttachedDisk.Attach(p.FilePath, readOnly: true, ct);
        var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
        p.VolumeTotal = total;
        p.VolumeUsed = total - free;
        p.AnalyzedAt = DateTime.Now;
        p.FsDirty = VolumeHelper.IsDirty(disk.VolumePath);
        if (p.FsDirty == true) Log.Warn($"Аналіз {p.FilePath}: файлова система позначена як «брудна» — потрібна перевірка");

        r.Stage("Аналіз папок", 0.06, 0.70);
        var (tree, files) = BuildFolderTree(CleanupEngine.ContainerRoot(disk.VolumePath, p.ContainerType), total - free, r, ct);
        p.SetFolderTree(tree);
        p.TopFiles = files;

        r.Stage("Оцінка правил очищення", 0.70, 0.98);
        var est = new CleanupEngine(rules, dryRun: true, r, ct).Run(disk.VolumePath, p.ContainerType);
        p.Estimates = est.PerRule.Where(e => e.Bytes > 0 || e.Files > 0).OrderByDescending(e => e.Bytes).ToList();

        r.Stage("Відключення диска", 0.98, 1);
        disk.Detach();
        r.Report(1);
        p.LastResult = $"Аналіз: зайнято {Format.Bytes(total - free)} з {Format.Bytes(total)}, можна звільнити ≈ {Format.Bytes(est.Bytes)}" +
                       (p.FsDirty == true ? ". Файлова система потребує перевірки — натисніть «Перевірити ФС»." : "");
        Log.Info($"Аналіз {p.FilePath}: used {Format.Bytes(total - free)}/{Format.Bytes(total)}, правила звільнять ≈ {Format.Bytes(est.Bytes)} ({est.Files} файлів)");
    }

    /// <summary>
    /// Quick read-only measurement: used/total space and the NTFS dirty bit, without walking the folders.
    /// Takes seconds — the headless "compact" uses it to decide whether a container is worth compacting.
    /// </summary>
    public static void Measure(ProfileInfo p, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.1);
        EnsureFree(p);
        r.Stage("Підключення диска (лише читання)", 0.1, 0.8);
        using var disk = AttachedDisk.Attach(p.FilePath, readOnly: true, ct);
        var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
        p.VolumeTotal = total;
        p.VolumeUsed = total - free;
        p.AnalyzedAt = DateTime.Now;
        p.FsDirty = VolumeHelper.IsDirty(disk.VolumePath);
        r.Stage("Відключення диска", 0.8, 1);
        disk.Detach();
        r.Report(1);
        p.LastResult = $"Виміряно: зайнято {Format.Bytes(total - free)} з {Format.Bytes(total)}";
    }

    /// <summary>Applies cleanup rules (RW attach), re-trims and optionally compacts.</summary>
    public static void Clean(ProfileInfo p, CleanupRuleSet rules, bool compact, OpReporter r, CancellationToken ct)
    {
        // weights: cleanup dominates without compaction; with it, compaction is the long part
        double cleanEnd = compact ? 0.50 : 0.90, trimEnd = compact ? 0.56 : 0.97;

        r.Stage("Перевірка стану", 0, 0.01);
        EnsureFree(p);
        long before = SafeLength(p.FilePath);
        Log.Info($"Очищення {p.FilePath} ({p.DisplayUser}), розмір файлу {Format.Bytes(before)}");

        CleanupStats stats;
        r.Stage("Підключення диска", 0.01, 0.04);
        using (var disk = AttachedDisk.Attach(p.FilePath, readOnly: false, ct))
        {
            var (_, freeBefore) = VolumeHelper.GetSpace(disk.VolumePath);
            r.Stage("Видалення за правилами", 0.04, cleanEnd);
            stats = new CleanupEngine(rules, dryRun: false, r, ct).Run(disk.VolumePath, p.ContainerType);

            r.Stage("ReTrim тому", cleanEnd, trimEnd);
            VolumeHelper.Flush(disk.VolumePath);
            ReTrim(disk.VolumePath);
            var (total, freeAfter) = VolumeHelper.GetSpace(disk.VolumePath);
            p.VolumeTotal = total;
            p.VolumeUsed = total - freeAfter;
            p.AnalyzedAt = DateTime.Now;
            Log.Info($"  видалено {stats.Files} файлів / {stats.Folders} папок, {Format.Bytes(stats.Bytes)}; вільно в томі: {Format.Bytes(freeBefore)} → {Format.Bytes(freeAfter)}");
            r.Stage("Відключення диска", trimEnd, trimEnd + 0.02);
        }

        if (compact)
        {
            r.Stage("Стиснення VHDX", trimEnd + 0.02, 1);
            VirtualDisk.Compact(p.FilePath, (cur, of) => r.Report((double)cur / of, $"{cur * 100 / Math.Max(1, of)} %"), ct);
        }

        long after = SafeLength(p.FilePath);
        p.FileSize = after;
        p.LastWrite = File.GetLastWriteTime(p.FilePath);
        p.Estimates = null;
        p.TopFiles = null;
        p.SetFolderTree(null);   // sizes changed — re-analyse to browse again
        p.LastResult = $"Видалено {stats.Files} файлів ({Format.Bytes(stats.Bytes)})" +
                       (stats.Errors > 0 ? $", помилок {stats.Errors}" : "") +
                       $". Файл: {Format.Bytes(before)} → {Format.Bytes(after)}";
        Log.Info($"  файл контейнера: {Format.Bytes(before)} → {Format.Bytes(after)} (−{Format.Bytes(before - after)})");
    }

    /// <summary>
    /// Deletes one folder INSIDE the container (RW attach), re-trims and optionally compacts.
    /// Uses the cleanup engine: junctions are not followed, global excludes (NTUSER.DAT…) are honoured,
    /// restrictive ACLs are handled with backup semantics.
    /// </summary>
    public static void DeleteFolder(ProfileInfo p, string relPath, CleanupRuleSet rules, bool compact, OpReporter r, CancellationToken ct)
    {
        relPath = relPath.Trim('\\');
        if (FolderNode.IsProtected(relPath) || relPath.Split('\\').Any(s => s is "." or "..") ||
            relPath.StartsWith("$volume", StringComparison.OrdinalIgnoreCase) || relPath.IndexOfAny(new[] { '*', '?' }) >= 0)
            throw new InvalidOperationException($"Папку «{relPath}» видаляти заборонено.");

        double delEnd = compact ? 0.45 : 0.90, trimEnd = compact ? 0.52 : 0.97;
        r.Stage("Перевірка стану", 0, 0.01);
        EnsureFree(p);
        long before = SafeLength(p.FilePath);

        CleanupStats stats;
        bool leftovers;
        r.Stage("Підключення диска", 0.01, 0.04);
        using (var disk = AttachedDisk.Attach(p.FilePath, readOnly: false, ct))
        {
            var root = CleanupEngine.ContainerRoot(disk.VolumePath, p.ContainerType);
            var full = Path.Combine(root, relPath);
            var di = new DirectoryInfo(full);
            if (!di.Exists) throw new InvalidOperationException($"Папки «{relPath}» у контейнері вже немає.");
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"«{relPath}» — це посилання (junction), а не папка.");

            Log.Warn($"Видалення папки «{relPath}» з контейнера {p.FilePath} ({p.DisplayUser})");
            var oneRule = new CleanupRuleSet
            {
                GlobalExclude = rules.GlobalExclude,
                Rules = { new CleanupRule { Name = $"Папка {relPath}", Paths = { relPath }, Action = CleanupAction.DeleteFolder } },
            };
            r.Stage("Видалення папки", 0.04, delEnd);
            stats = new CleanupEngine(oneRule, dryRun: false, r, ct).Run(disk.VolumePath, p.ContainerType);
            leftovers = Directory.Exists(full);

            r.Stage("ReTrim тому", delEnd, trimEnd);
            VolumeHelper.Flush(disk.VolumePath);
            ReTrim(disk.VolumePath);
            var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
            p.VolumeTotal = total;
            p.VolumeUsed = total - free;
            p.AnalyzedAt = DateTime.Now;
            r.Stage("Відключення диска", trimEnd, trimEnd + 0.02);
        }

        if (compact)
        {
            r.Stage("Стиснення VHDX", trimEnd + 0.02, 1);
            VirtualDisk.Compact(p.FilePath, (cur, of) => r.Report((double)cur / of, $"{cur * 100 / Math.Max(1, of)} %"), ct);
        }

        long after = SafeLength(p.FilePath);
        p.FileSize = after;
        p.LastWrite = File.GetLastWriteTime(p.FilePath);
        if (!leftovers) p.RemoveFolder(relPath);
        p.Estimates = null;
        p.LastResult = $"Папку «{relPath}» видалено: {stats.Files:N0} файлів, {Format.Bytes(stats.Bytes)}" +
                       (leftovers ? $" (частину залишено: виключення або помилки — {stats.Errors})" : "") +
                       $". Файл: {Format.Bytes(before)} → {Format.Bytes(after)}";
        Log.Info($"  папку «{relPath}»: {stats.Files} файлів, {Format.Bytes(stats.Bytes)}, помилок {stats.Errors}; файл {Format.Bytes(before)} → {Format.Bytes(after)}");
    }

    /// <summary>
    /// Raises the maximum size of the container: grows the VHD(X) while detached, then attaches it RW and
    /// extends the partition to the new end. FSLogix SizeInMBs only applies to NEW containers, so this is how
    /// an existing user gets more space. Hyper-V is not needed.
    /// </summary>
    public static void Grow(ProfileInfo p, long newSize, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.03);
        EnsureFree(p);
        long current = VhdHeader.TryReadVirtualSize(p.FilePath) ?? p.VirtualSize ?? 0;
        if (current <= 0) throw new InvalidOperationException("Не вдалося прочитати поточний максимальний розмір із заголовка.");
        newSize = (newSize + (1L << 20) - 1) & ~((1L << 20) - 1);   // whole MB
        if (newSize <= current)
            throw new OperationSkippedException($"поточний максимум {Format.Bytes(current)} уже не менший за {Format.Bytes(newSize)}.");
        if (p.DiskFormat == "VHD" && newSize > 2040L << 30)
            throw new InvalidOperationException("Формат VHD обмежений 2040 GB.");

        Log.Warn($"Збільшення {p.FilePath} ({p.DisplayUser}): {Format.Bytes(current)} → {Format.Bytes(newSize)}");
        r.Stage("Збільшення віртуального диска", 0.03, 0.30);
        VirtualDisk.Grow(p.FilePath, (ulong)newSize);
        p.VirtualSize = newSize;

        r.Stage("Підключення диска", 0.30, 0.45);
        using (var disk = AttachedDisk.Attach(p.FilePath, readOnly: false, ct))
        {
            r.Stage("Розширення розділу", 0.45, 0.90);
            (ulong before, ulong after) part;
            try { part = StorageWmi.ExtendPartition(disk.DiskNumber, disk.VolumePath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"VHDX збільшено до {Format.Bytes(newSize)}, але розділ не розширено: {ex.Message}. " +
                    "Дані цілі; повторіть операцію або розширте розділ вручну (diskpart → extend).", ex);
            }
            VolumeHelper.Flush(disk.VolumePath);
            var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
            p.VolumeTotal = total;
            p.VolumeUsed = total - free;
            p.AnalyzedAt = DateTime.Now;
            Log.Info($"  розділ: {Format.Bytes((long)part.before)} → {Format.Bytes((long)part.after)}; том {Format.Bytes(total)}, вільно {Format.Bytes(free)}");
            r.Stage("Відключення диска", 0.90, 1);
        }
        p.FileSize = SafeLength(p.FilePath);
        p.LastWrite = File.GetLastWriteTime(p.FilePath);
        r.Report(1);
        p.LastResult = $"Максимальний розмір: {Format.Bytes(current)} → {Format.Bytes(newSize)}, том {Format.Bytes(p.VolumeTotal ?? 0)}";
    }

    /// <summary>Offline check and fix of the NTFS volume inside the container (chkdsk /f equivalent).</summary>
    public static void RepairFs(ProfileInfo p, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.03);
        EnsureFree(p);
        r.Stage("Підключення диска", 0.03, 0.10);
        using var disk = AttachedDisk.Attach(p.FilePath, readOnly: false, ct);
        bool? dirtyBefore = VolumeHelper.IsDirty(disk.VolumePath);
        Log.Warn($"Перевірка ФС {p.FilePath} ({p.DisplayUser}), dirty = {dirtyBefore?.ToString() ?? "?"}");

        r.Stage("Перевірка й виправлення (chkdsk)", 0.10, 0.92);
        var (result, output) = StorageWmi.Repair(disk.VolumePath);
        ct.ThrowIfCancellationRequested();

        r.Stage("Відключення диска", 0.92, 1);
        p.FsDirty = VolumeHelper.IsDirty(disk.VolumePath);
        var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
        p.VolumeTotal = total;
        p.VolumeUsed = total - free;
        p.AnalyzedAt = DateTime.Now;
        disk.Detach();
        p.FileSize = SafeLength(p.FilePath);
        p.LastWrite = File.GetLastWriteTime(p.FilePath);
        r.Report(1);

        var text = result switch
        {
            StorageWmi.RepairResult.NoErrors => "помилок не знайдено",
            StorageWmi.RepairResult.Fixed => "помилки знайдено й виправлено",
            _ => $"результат {output} — перегляньте журнал подій (Application, джерело Chkdsk)",
        };
        p.LastResult = $"Перевірка ФС: {text}" + (p.FsDirty == true ? ". Том досі позначено як «брудний»." : ".");
        Log.Info($"  перевірка ФС: {text}, dirty після = {p.FsDirty?.ToString() ?? "?"}");
    }

    /// <summary>
    /// Deletes ONE file inside the container (RW attach), re-trims and optionally compacts.
    /// Goes through the cleanup engine (rule "DeleteFiles" with the exact name in its folder), so the same safeguards apply:
    /// globalExclude (NTUSER.DAT…) is never deleted, junctions are not followed, restrictive ACLs are handled.
    /// </summary>
    public static void DeleteFileIn(ProfileInfo p, string relPath, CleanupRuleSet rules, bool compact, OpReporter r, CancellationToken ct)
    {
        relPath = relPath.Trim('\\');
        var name = Path.GetFileName(relPath);
        if (name.Length == 0 || relPath.Split('\\').Any(s => s is "." or "..") || relPath.IndexOfAny(new[] { '*', '?' }) >= 0 ||
            relPath.StartsWith("$volume", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Файл «{relPath}» видаляти заборонено.");
        if (rules.GlobalExclude.Any(m => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(m, name)))
            throw new InvalidOperationException($"«{name}» захищено правилом globalExclude — такі файли не видаляються.");

        double delEnd = compact ? 0.10 : 0.80, trimEnd = compact ? 0.20 : 0.95;
        r.Stage("Перевірка стану", 0, 0.01);
        EnsureFree(p);
        long before = SafeLength(p.FilePath);

        long size;
        CleanupStats stats;
        r.Stage("Підключення диска", 0.01, 0.04);
        using (var disk = AttachedDisk.Attach(p.FilePath, readOnly: false, ct))
        {
            var root = CleanupEngine.ContainerRoot(disk.VolumePath, p.ContainerType);
            var fi = new FileInfo(Path.Combine(root, relPath));
            if (!fi.Exists) throw new InvalidOperationException($"Файлу «{relPath}» у контейнері вже немає.");
            if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"«{relPath}» — це посилання, а не файл.");
            size = fi.Length;

            Log.Warn($"Видалення файлу «{relPath}» ({Format.Bytes(size)}) з контейнера {p.FilePath} ({p.DisplayUser})");
            r.Stage("Видалення файлу", 0.04, delEnd);
            stats = DeleteOneFile(disk.VolumePath, p.ContainerType, relPath, rules, r, ct);
            if (File.Exists(fi.FullName))
                throw new InvalidOperationException($"Файл «{relPath}» не видалено" + (stats.Errors > 0 ? " (помилка доступу або файл зайнятий)." : "."));

            r.Stage("ReTrim тому", delEnd, trimEnd);
            VolumeHelper.Flush(disk.VolumePath);
            ReTrim(disk.VolumePath);
            var (total, free) = VolumeHelper.GetSpace(disk.VolumePath);
            p.VolumeTotal = total;
            p.VolumeUsed = total - free;
            p.AnalyzedAt = DateTime.Now;
            r.Stage("Відключення диска", trimEnd, trimEnd + 0.02);
        }

        if (compact)
        {
            r.Stage("Стиснення VHDX", trimEnd + 0.02, 1);
            VirtualDisk.Compact(p.FilePath, (cur, of) => r.Report((double)cur / of, $"{cur * 100 / Math.Max(1, of)} %"), ct);
        }

        long after = SafeLength(p.FilePath);
        p.FileSize = after;
        p.LastWrite = File.GetLastWriteTime(p.FilePath);
        p.RemoveFile(relPath, size);
        p.Estimates = null;   // the file may have been counted by a rule
        p.LastResult = $"Файл «{name}» видалено ({Format.Bytes(size)}). Файл контейнера: {Format.Bytes(before)} → {Format.Bytes(after)}";
        Log.Info($"  файл «{relPath}»: {Format.Bytes(size)}; файл контейнера {Format.Bytes(before)} → {Format.Bytes(after)}");
    }

    /// <summary>
    /// The deletion itself on an attached volume (separate, so it can be tested on a plain folder):
    /// rule "DeleteFiles" with the exact file name in its folder. A file lying in the container root is addressed via
    /// $volume\&lt;root folder&gt;, because the engine never targets the root by an empty path.
    /// </summary>
    internal static CleanupStats DeleteOneFile(string volumePath, ContainerType type, string relPath, CleanupRuleSet rules,
                                               OpReporter r, CancellationToken ct)
    {
        var root = CleanupEngine.ContainerRoot(volumePath, type);
        var name = Path.GetFileName(relPath);
        var folder = Path.GetDirectoryName(relPath) ?? "";
        string folderPattern;
        if (folder.Length > 0) folderPattern = folder;
        else if (!root.TrimEnd('\\').Equals(volumePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            folderPattern = @"$volume\" + Path.GetFileName(root.TrimEnd('\\'));
        else throw new InvalidOperationException("Файли в корені тому цим способом не видаляються.");

        var oneRule = new CleanupRuleSet
        {
            GlobalExclude = rules.GlobalExclude,
            Rules = { new CleanupRule { Name = $"Файл {relPath}", Paths = { folderPattern }, Action = CleanupAction.DeleteFiles, Include = { name } } },
        };
        return new CleanupEngine(oneRule, dryRun: false, r, ct).Run(volumePath, type);
    }

    public static void Compact(ProfileInfo p, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.02);
        EnsureFree(p);
        long before = SafeLength(p.FilePath);
        Log.Info($"Стиснення {p.FilePath}, {Format.Bytes(before)}");
        r.Stage("Стиснення VHDX", 0.02, 1);
        VirtualDisk.Compact(p.FilePath, (cur, of) => r.Report((double)cur / of, $"{cur * 100 / Math.Max(1, of)} %"), ct);
        long after = SafeLength(p.FilePath);
        p.FileSize = after;
        p.LastResult = $"Стиснуто: {Format.Bytes(before)} → {Format.Bytes(after)}";
        Log.Info($"  {Format.Bytes(before)} → {Format.Bytes(after)} (−{Format.Bytes(before - after)})");
    }

    /// <summary>
    /// Permanently deletes a container that is stale OR whose account is disabled / expired in AD. Safety:
    ///  • re-checks right now that nobody uses it and that it still qualifies: stale by a fresh LastWriteTime,
    ///    or — if it is not stale — the account is STILL disabled / expired by a fresh AD query
    ///    (an unreachable domain controller means "not confirmed" → skipped);
    ///  • deletes the user folder only if no other container (Profile_/ODFC_) is left in it,
    ///    otherwise only this container and its sidecar files (.lock/.meta/…);
    ///  • never deletes the configured root folder itself; never follows junctions/symlinks.
    /// </summary>
    public static void Delete(ProfileInfo p, int staleDays, OpReporter r, CancellationToken ct)
    {
        r.Stage("Перевірка стану", 0, 0.05);
        EnsureFree(p);
        var fi = new FileInfo(p.FilePath);
        if (!fi.Exists) throw new InvalidOperationException("Файл контейнера вже не існує.");
        var age = (DateTime.Now - fi.LastWriteTime).TotalDays;
        string reason;
        if (age > staleDays) reason = $"змінювався {age:0} дн. тому";
        else
        {
            p.LastWrite = fi.LastWriteTime;
            // not stale — allowed only for an account that is disabled / expired in AD; ask AD again right now
            r.Stage("Перевірка облікового запису в AD", 0.03, 0.05);
            AdService.Resolve(new[] { p }, ct, force: true);
            if (!p.IsDisabled)
                throw new OperationSkippedException($"контейнер змінювався {age:0} дн. тому, а обліковий запис в AD не вимкнений " +
                                                    "(або стан в AD не вдалося перевірити).");
            reason = $"{p.AdStateText}, змінювався {age:0} дн. тому";
        }

        var dir = fi.Directory!;
        var root = Path.GetFullPath(p.RootPath).TrimEnd('\\');
        bool folderIsUserFolder = !dir.FullName.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                  dir.FullName.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

        // other containers that would be lost with the folder
        var others = dir.EnumerateFiles("*.vhd*")
            .Where(f => (f.Extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
                        && !f.FullName.Equals(fi.FullName, StringComparison.OrdinalIgnoreCase)
                        && !IsDiffDisk(f.Name))
            .Select(f => f.Name).ToList();

        long size = fi.Length;
        if (folderIsUserFolder && others.Count == 0)
        {
            r.Stage("Видалення папки профілю", 0.05, 1);
            Log.Warn($"ВИДАЛЕННЯ профілю {p.DisplayUser}: папка {dir.FullName} ({Format.Bytes(size)}, {reason})");
            DeleteTree(dir, r, ct);
            p.LastResult = $"Видалено папку {dir.Name}";
        }
        else
        {
            r.Stage("Видалення контейнера", 0.05, 1);
            var sidecars = dir.EnumerateFiles(fi.Name + ".*").ToList();   // Profile_x.VHDX.lock / .meta / …
            Log.Warn($"ВИДАЛЕННЯ контейнера {fi.FullName} ({Format.Bytes(size)}, {reason})" +
                     (others.Count > 0 ? $"; папку збережено — в ній є {string.Join(", ", others)}" : "; файл лежить у корені — папку не чіпаємо"));
            DeleteFile(fi);
            foreach (var s in sidecars) DeleteFile(s);
            p.LastResult = others.Count > 0 ? $"Видалено файл; папку збережено ({string.Join(", ", others)})" : "Видалено файл";
        }
        r.Report(1);
        p.IsDeleted = true;
    }

    private static bool IsDiffDisk(string name) =>
        name.Equals("RW.VHDX", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".RW.VHDX", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-RW.VHDX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Recursive delete that removes links themselves and never descends into them.</summary>
    private static void DeleteTree(DirectoryInfo root, OpReporter r, CancellationToken ct)
    {
        var opt = new System.IO.EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };
        var all = new List<FileSystemInfo>();
        void Collect(DirectoryInfo d)
        {
            foreach (var e in d.EnumerateFileSystemInfos("*", opt))
            {
                if (e is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) Collect(sub);
                all.Add(e);   // children before their parent
            }
        }
        Collect(root);
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = all[i];
            if (e is DirectoryInfo d) { ClearAttrs(d); d.Delete(false); }
            else DeleteFile((FileInfo)e);
            r.Report((double)(i + 1) / (all.Count + 1), e.Name);
        }
        ClearAttrs(root);
        root.Delete(false);
    }

    private static void DeleteFile(FileInfo f)
    {
        ClearAttrs(f);
        f.Delete();
    }

    private static void ClearAttrs(FileSystemInfo e)
    {
        const FileAttributes ro = FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden;
        if ((e.Attributes & ro) != 0) e.Attributes &= ~ro;
    }

    /// <summary>Force-closes SMB handles (user session loses its profile!) or detaches a local attach.</summary>
    public static void Unmount(ProfileInfo p)
    {
        var local = PathMapper.ToLocal(p.FilePath);
        var smb = SmbService.GetOpenFiles().Where(h => h.LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var h in smb)
        {
            Log.Warn($"Примусове закриття SMB-дескриптора {h.FileId}: {h.LocalPath} ({h.ClientComputer} / {h.ClientUser})");
            SmbService.ForceClose(h);
        }
        if (smb.Count == 0 && VirtualDisk.IsAttachedLocally(p.FilePath))
        {
            Log.Warn($"Локальне відключення {p.FilePath}");
            VirtualDisk.DetachLocal(p.FilePath);
        }
        else if (smb.Count == 0 && p.State == MountState.Locked)
        {
            throw new InvalidOperationException("Файл заблокований процесом, який не є SMB-сесією чи локальним підключенням. " +
                                                "Перевірте Resource Monitor → CPU → Associated Handles.");
        }
        Thread.Sleep(500);
        p.SetHandles(Array.Empty<SmbHandle>());
        p.State = ProfileScanner.ProbeLock(p.FilePath);
        p.LastResult = p.State == MountState.Free ? "Відмонтовано" : "Після відмонтування стан: " + p.StateText;
    }

    private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static void ReTrim(string volumePath)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var wql = "SELECT * FROM MSFT_Volume WHERE Path = '" + volumePath.Replace(@"\", @"\\") + "'";
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
            foreach (ManagementObject v in s.Get())
            {
                using (v)
                {
                    var inParams = v.GetMethodParameters("Optimize");
                    inParams["ReTrim"] = true;
                    v.InvokeMethod("Optimize", inParams, null);
                    return;
                }
            }
            Log.Warn("ReTrim: том не знайдено у MSFT_Volume");
        }
        catch (Exception ex)
        {
            // Not fatal: NTFS already sends TRIM on delete, compaction still reclaims space.
            Log.Warn("ReTrim не виконано: " + ex.Message);
        }
    }

    /// <summary>
    /// Builds the folder size tree of the container (one walk over all files).
    /// Progress = bytes measured so far / bytes used on the volume.
    /// The same walk collects the largest files (≥ <see cref="FileUsage.MinSize"/>, top <see cref="FileUsage.MaxCount"/>).
    /// </summary>
    internal static (FolderNode tree, List<FileUsage> files) BuildFolderTree(string root, long usedBytes, OpReporter r, CancellationToken ct)
    {
        var w = new TreeWalker(usedBytes, r, ct);
        var tree = w.Walk(new DirectoryInfo(root), "", "");
        return (tree, w.TopFiles());
    }

    private sealed class TreeWalker(long usedBytes, OpReporter r, CancellationToken ct)
    {
        private long _seen;
        /// <summary>Min-heap by size: the smallest of the current top files is dequeued first.</summary>
        private readonly PriorityQueue<FileUsage, long> _top = new();

        public List<FileUsage> TopFiles() =>
            _top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();

        private void Consider(FileInfo f, string relPath)
        {
            long len = f.Length;
            if (len < FileUsage.MinSize) return;
            lock (_top)
            {
                if (_top.Count >= FileUsage.MaxCount && (!_top.TryPeek(out _, out var min) || len <= min)) return;
                var u = new FileUsage
                {
                    Path = relPath.Length == 0 ? f.Name : relPath + "\\" + f.Name,
                    Size = len,
                    Modified = f.LastWriteTime,
                };
                if (_top.Count >= FileUsage.MaxCount) _top.EnqueueDequeue(u, len);
                else _top.Enqueue(u, len);
            }
        }
        private readonly System.IO.EnumerationOptions _opt = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

        /// <summary>
        /// The top levels (e.g. Desktop / AppData → Local / Roaming → vendors) are walked on several threads:
        /// NTFS directory reads inside one VHDX overlap well, and a profile's bulk sits in a few big subtrees.
        /// Deeper levels stay sequential to keep the thread count bounded.
        /// </summary>
        private const int ParallelDepth = 3, ParallelDegree = 4;

        public FolderNode Walk(DirectoryInfo d, string name, string relPath, int depth = 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = new FolderNode { Name = name, Path = relPath };
            var subs = new List<DirectoryInfo>();
            foreach (var e in Safe(() => d.EnumerateFileSystemInfos("*", _opt)))
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // never follow junctions
                if (e is FileInfo f)
                {
                    node.FilesSize += f.Length;
                    Consider(f, relPath);
                    long seen = Interlocked.Add(ref _seen, f.Length);
                    if (usedBytes > 0) r.Report((double)seen / usedBytes, $"{relPath} · {Format.Bytes(seen)} з {Format.Bytes(usedBytes)}");
                }
                else if (e is DirectoryInfo sub) subs.Add(sub);
            }

            string Rel(DirectoryInfo s) => relPath.Length == 0 ? s.Name : relPath + "\\" + s.Name;
            var children = new FolderNode[subs.Count];
            if (depth < ParallelDepth && subs.Count > 1)
                Parallel.For(0, subs.Count, new ParallelOptions { MaxDegreeOfParallelism = ParallelDegree, CancellationToken = ct },
                    i => children[i] = Walk(subs[i], subs[i].Name, Rel(subs[i]), depth + 1));
            else
                for (int i = 0; i < subs.Count; i++) children[i] = Walk(subs[i], subs[i].Name, Rel(subs[i]), depth + 1);

            // folded in listing order, on this thread — the tree itself is never touched concurrently
            foreach (var child in children)
            {
                node.Size += child.Size;
                if (child.Size >= FolderNode.Threshold) { child.Parent = node; node.Children.Add(child); }
                else { node.SmallCount++; node.SmallSize += child.Size; }   // folded — subtree dropped
            }
            node.Size += node.FilesSize;
            node.Children.Sort((x, y) => y.Size.CompareTo(x.Size));
            return node;
        }
    }

    private static List<T> Safe<T>(Func<IEnumerable<T>> f)
    {
        try { return f().ToList(); } catch { return new List<T>(); }
    }

    public static void OpenInExplorer(ProfileInfo p) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p.FilePath}\"") { UseShellExecute = true });
}
