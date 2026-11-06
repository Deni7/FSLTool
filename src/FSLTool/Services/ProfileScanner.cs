using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>
/// Scanning is split into phases so the list appears almost immediately and slow steps
/// (headers, SMB, AD lookups) fill in afterwards:
///   1. Discover    — directory listing only (size/date come from the listing, no per-file I/O)
///   2. ReadHeaders — max size from VHD(X) header, only for files not in the persistent cache
///   3. UpdateStates— one SMB query for all files + lock probe for the rest
///   4. ResolveAccounts — one batched LSA lookup for all SIDs
/// </summary>
public static partial class ProfileScanner
{
    [GeneratedRegex(@"S-1-\d+(-\d+)+", RegexOptions.IgnoreCase)]
    private static partial Regex SidRegex();

    /// <summary>I/O bound work (mostly waiting on disk/SMB), so more threads than cores is fine.</summary>
    private const int IoParallelism = 32;

    private static readonly ConcurrentDictionary<string, (string? name, bool orphan)> _accounts = new();

    // ───────────────────────────── 1. discover ─────────────────────────────

    /// <summary>Finds all containers under the given roots (root\*.vhdx and root\*\*.vhdx).</summary>
    public static List<ProfileInfo> Discover(IEnumerable<string> roots, Dictionary<string, long> sizeCache, CancellationToken ct)
    {
        var result = new ConcurrentBag<ProfileInfo>();
        var opt = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var rootDir = new DirectoryInfo(root);
            if (!rootDir.Exists) { Log.Warn($"Шлях недоступний: {root}"); continue; }

            List<DirectoryInfo> dirs;
            try { dirs = rootDir.EnumerateDirectories("*", opt).ToList(); }
            catch (Exception ex) { Log.Warn($"{root}: {ex.Message}"); continue; }
            dirs.Add(rootDir); // containers lying directly in the root

            // one directory listing per user folder; FileInfo from the listing already carries size + dates
            Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism, CancellationToken = ct }, dir =>
            {
                List<FileInfo> files;
                try
                {
                    files = dir.EnumerateFiles("*.vhd*", opt)
                        .Where(f => f.Extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase) ||
                                    f.Extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                catch { return; }

                bool hasDiff = files.Any(f => IsDiffDisk(f.Name));
                foreach (var f in files.Where(f => !IsDiffDisk(f.Name)))
                {
                    var p = Create(root, f.FullName);
                    p.FileSize = f.Length;
                    p.LastWrite = f.LastWriteTime;
                    p.HasDiffDisk = hasDiff;
                    lock (sizeCache)
                        if (sizeCache.TryGetValue(p.FilePath, out var vs)) p.VirtualSize = vs;
                    result.Add(p);
                }
            });
        }
        return result.ToList();
    }

    // ───────────────────────────── 2. headers ─────────────────────────────

    /// <summary>Reads max size from headers for containers whose size is not cached yet. Returns how many were read.</summary>
    public static int ReadHeaders(IList<ProfileInfo> profiles, Dictionary<string, long> sizeCache, Action<int, int>? progress, CancellationToken ct)
    {
        var todo = profiles.Where(p => p.VirtualSize == null).ToList();
        int done = 0;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism, CancellationToken = ct }, p =>
        {
            // the maximum size of an existing VHDX practically never changes, so it is cached forever
            var vs = VhdHeader.TryReadVirtualSize(p.FilePath);
            if (vs is > 0)
            {
                lock (sizeCache) sizeCache[p.FilePath] = vs.Value;
                p.VirtualSize = vs;
            }
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0) progress?.Invoke(n, todo.Count);
        });
        return todo.Count;
    }

    // ───────────────────────────── targeted refresh ─────────────────────────────

    /// <summary>
    /// Re-reads EVERYTHING for the given containers, bypassing caches: size/date, max size from the header,
    /// SMB/lock state, account from AD. Containers whose file disappeared are flagged IsDeleted.
    /// The volume analysis (attach) is NOT redone here — it keeps its own AnalyzedAt.
    /// </summary>
    public static void RefreshInfo(IList<ProfileInfo> profiles, Dictionary<string, long> sizeCache,
                                   Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("файли та заголовки…");
        Parallel.ForEach(profiles, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism, CancellationToken = ct }, p =>
        {
            var fi = new FileInfo(p.FilePath);
            if (!fi.Exists) { p.IsDeleted = true; return; }
            p.FileSize = fi.Length;
            p.LastWrite = fi.LastWriteTime;
            p.HasDiffDisk = fi.Directory!.EnumerateFiles("*.vhdx").Any(f => IsDiffDisk(f.Name));
            var vs = VhdHeader.TryReadVirtualSize(p.FilePath);   // header may be unreadable while mounted → keep cached
            if (vs is > 0)
            {
                lock (sizeCache) sizeCache[p.FilePath] = vs.Value;
                p.VirtualSize = vs;
            }
        });

        var alive = profiles.Where(p => !p.IsDeleted).ToList();
        progress?.Invoke("стан підключень…");
        UpdateStates(alive, ct);
        progress?.Invoke("облікові записи…");
        ResolveAccounts(alive, ct, force: true);
        progress?.Invoke("стан в AD…");
        AdService.Resolve(alive, ct, force: true);

        var now = DateTime.Now;
        foreach (var p in alive) p.RefreshedAt = now;
    }

    // ───────────────────────────── naming ─────────────────────────────

    private static bool IsDiffDisk(string name) =>
        name.Equals("RW.VHDX", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".RW.VHDX", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-RW.VHDX", StringComparison.OrdinalIgnoreCase);

    private static ProfileInfo Create(string root, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
        var type = name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) ? ContainerType.Profile
                 : name.StartsWith("ODFC", StringComparison.OrdinalIgnoreCase) ? ContainerType.ODFC
                 : ContainerType.Unknown;

        var sid = SidRegex().Match(folder) is { Success: true } m ? m.Value
                : SidRegex().Match(name) is { Success: true } m2 ? m2.Value : null;

        // user name: Profile_jdoe.vhdx → jdoe; otherwise folder "SID_jdoe" / "jdoe_SID"
        string user = "";
        var us = name.IndexOf('_');
        if (type != ContainerType.Unknown && us > 0) user = name[(us + 1)..];
        if (string.IsNullOrEmpty(user) && sid != null) user = folder.Replace(sid, "").Trim('_', '-', ' ');
        if (string.IsNullOrEmpty(user)) user = folder;

        return new ProfileInfo
        {
            FilePath = file,
            RootPath = root,
            ContainerType = type,
            Sid = sid,
            UserName = user,
        };
    }

    // ───────────────────────────── 4. accounts ─────────────────────────────

    /// <summary>
    /// Resolves SIDs to DOMAIN\user with ONE batched LsaLookupSids call (instead of one DC round trip per SID).
    /// Unresolvable SIDs are marked as orphans. Results are cached for the lifetime of the process.
    /// </summary>
    public static void ResolveAccounts(IEnumerable<ProfileInfo> profiles, CancellationToken ct, bool force = false)
    {
        var withSid = profiles.Where(p => p.Sid != null).ToList();
        if (force) foreach (var p in withSid) _accounts.TryRemove(p.Sid!, out _);   // re-ask AD for these
        var unknown = withSid.Select(p => p.Sid!).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(s => !_accounts.ContainsKey(s)).ToList();

        foreach (var chunk in unknown.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            var sids = new List<SecurityIdentifier>();
            foreach (var s in chunk)
            {
                try { sids.Add(new SecurityIdentifier(s)); }
                catch { _accounts[s] = (null, false); }
            }
            if (sids.Count == 0) continue;

            var coll = new IdentityReferenceCollection(sids.Count);
            foreach (var s in sids) coll.Add(s);
            try
            {
                // forceSuccess:false → unmapped SIDs come back as SecurityIdentifier instead of throwing
                var translated = coll.Translate(typeof(NTAccount), false);
                for (int i = 0; i < sids.Count; i++)
                {
                    var t = translated[i];
                    _accounts[sids[i].Value] = t is NTAccount acc ? (acc.Value, false) : (null, true);
                }
            }
            catch (Exception ex)
            {
                // DC unreachable etc. — don't call anything an orphan; retry on next scan
                Log.Warn("Пошук облікових записів у AD не вдався: " + ex.Message);
                return;
            }
        }

        foreach (var p in withSid)
        {
            if (!_accounts.TryGetValue(p.Sid!, out var r)) continue;
            p.AccountName = r.name;
            p.IsOrphan = r.orphan;
        }
    }

    // ───────────────────────────── 3. states ─────────────────────────────

    /// <summary>Determines who holds each container: SMB clients, local attach or unknown lock.</summary>
    public static void UpdateStates(IList<ProfileInfo> profiles, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        List<SmbHandle> handles;
        try { handles = SmbService.GetOpenFiles(); }
        catch (Exception ex) { Log.Warn("Не вдалося отримати відкриті SMB-файли: " + ex.Message); handles = new(); }
        var smbMs = sw.ElapsedMilliseconds;

        var byPath = handles.GroupBy(h => h.LocalPath, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => (IReadOnlyList<SmbHandle>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        // virtual disks attached on THIS server — one call instead of opening every container file
        var attached = VirtualDisk.GetAttachedBackingFiles();
        var attachMs = sw.ElapsedMilliseconds - smbMs;
        if (attached == null) Log.Warn("Список локально підключених дисків недоступний — стан «Локально» визначиться перед дією");

        // Containers on THIS server are NOT opened during the scan: with hundreds of VHDX files that took minutes
        // (antivirus scans each file on open; RDS hosts holding a file must first give up their SMB lease).
        // "In use" comes from SMB, "attached here" from virtdisk. A lock by some other local process is still
        // caught right before every operation (EnsureFree opens that one file).
        // Containers on ANOTHER server: SMB opens are not visible from here — the open probe is the only signal.
        int probed = 0;
        Parallel.ForEach(profiles, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism, CancellationToken = ct }, p =>
        {
            var local = PathMapper.ToLocal(p.FilePath);
            if (byPath.TryGetValue(local, out var hs))
            {
                p.SetHandles(hs);
                p.State = MountState.InUseSmb;
                return;
            }
            if (p.Handles.Count > 0) p.SetHandles(Array.Empty<SmbHandle>());
            if (local.StartsWith(@"\\"))
            {
                Interlocked.Increment(ref probed);
                p.State = ProbeLock(p.FilePath, isLocal: false);
            }
            else
                p.State = attached?.Contains(local) == true ? MountState.AttachedLocal : MountState.Free;
        });
        Log.Info($"  стан підключень: SMB-запит {smbMs} мс ({SmbService.LastTimings}; {handles.Count} VHD-дескрипторів), " +
                 $"локально підключені {attachMs} мс ({attached?.Count.ToString() ?? "—"}), " +
                 $"перевірка блокувань {sw.ElapsedMilliseconds - smbMs - attachMs} мс ({probed} файлів на інших серверах)");
    }

    /// <summary>Checks whether anybody has the file open for writing (attached somewhere).</summary>
    public static MountState ProbeLock(string path, bool isLocal = true)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            return MountState.Free;
        }
        catch (FileNotFoundException) { return MountState.Unknown; }
        catch (IOException)
        {
            // a local attach can only exist for files on this server; skip the (slow) virtdisk call for remote paths
            return isLocal && VirtualDisk.IsAttachedLocally(path) ? MountState.AttachedLocal : MountState.Locked;
        }
        catch (UnauthorizedAccessException) { return MountState.Unknown; }
    }
}
