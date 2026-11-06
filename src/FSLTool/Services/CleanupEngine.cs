using System.IO.Enumeration;
using FSLTool.Models;
using FSLTool.Native;

namespace FSLTool.Services;

public sealed class CleanupStats
{
    public long Bytes;
    public int Files;
    public int Folders;
    public int Errors;
    public List<RuleEstimate> PerRule { get; } = new();
}

/// <summary>
/// Applies cleanup rules to a mounted container volume. Never follows junctions/symlinks,
/// never leaves the container root, honours global and per-rule excludes.
/// </summary>
public sealed class CleanupEngine
{
    private readonly CleanupRuleSet _rules;
    private readonly bool _dryRun;
    private readonly OpReporter _progress;
    private readonly CancellationToken _ct;
    // live totals for the progress text
    private long _liveBytes;
    private int _liveFiles;
    private double _fraction;
    private string _where = "";
    private readonly DateTime _now = DateTime.Now;

    private static readonly EnumerationOptions Opt = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    static CleanupEngine()
    {
        // Lets us enumerate/delete regardless of profile ACLs (we are admin).
        NativeMethods.EnablePrivilege("SeBackupPrivilege");
        NativeMethods.EnablePrivilege("SeRestorePrivilege");
        NativeMethods.EnablePrivilege("SeTakeOwnershipPrivilege");
    }

    public CleanupEngine(CleanupRuleSet rules, bool dryRun, OpReporter? progress, CancellationToken ct)
    { _rules = rules; _dryRun = dryRun; _progress = progress ?? OpReporter.None; _ct = ct; }

    private void Tick() =>
        _progress.Report(_fraction, Loc.T(_dryRun ? "Ops.Cleanup.ProgressFound" : "Ops.Cleanup.ProgressDeleted", _where, _liveFiles, Format.Bytes(_liveBytes)));

    /// <summary>Folder inside the volume that holds the profile: "Profile" or "ODFC" (or the volume root).</summary>
    public static string ContainerRoot(string volume, ContainerType type)
    {
        foreach (var n in type == ContainerType.ODFC ? new[] { "ODFC", "Profile" } : new[] { "Profile", "ODFC" })
        {
            var p = Path.Combine(volume, n);
            if (Directory.Exists(p)) return p;
        }
        return volume;
    }

    public CleanupStats Run(string volume, ContainerType type)
    {
        var stats = new CleanupStats();
        var root = ContainerRoot(volume, type);
        var active = _rules.Rules.Where(r => r.Enabled && r.AppliesTo(type)).ToList();
        for (int ri = 0; ri < active.Count; ri++)
        {
            var rule = active[ri];
            _ct.ThrowIfCancellationRequested();
            var rs = new CleanupStats();
            for (int pi = 0; pi < rule.Paths.Count; pi++)
            {
                var pattern = rule.Paths[pi];
                // progress: rule index + share of its paths done
                _fraction = (ri + (double)pi / Math.Max(1, rule.Paths.Count)) / Math.Max(1, active.Count);
                _where = $"{rule.DisplayName} ({ri + 1}/{active.Count})";
                Tick();
                string baseDir = root, rel = pattern;
                if (pattern.StartsWith("$volume", StringComparison.OrdinalIgnoreCase))
                { baseDir = volume; rel = pattern[7..]; }
                var segs = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length == 0 || segs.Any(s => s is "." or "..")) continue;   // never the root itself

                foreach (var target in Expand(baseDir, segs, 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (!IsInside(target, baseDir)) continue;
                    Apply(rule, target, rs);
                }
            }
            stats.Bytes += rs.Bytes; stats.Files += rs.Files; stats.Folders += rs.Folders; stats.Errors += rs.Errors;
            stats.PerRule.Add(new RuleEstimate { RuleName = rule.Name, Bytes = rs.Bytes, Files = rs.Files });
            if (!_dryRun && rs.Files + rs.Folders > 0)
                Log.Info(Loc.T(rs.Errors > 0 ? "Ops.Cleanup.RuleLogErrors" : "Ops.Cleanup.RuleLog",
                               rule.DisplayName, rs.Files, rs.Folders, Format.Bytes(rs.Bytes), rs.Errors));
        }
        _fraction = 1;
        _where = Loc.T("Ops.Cleanup.Done");
        Tick();
        return stats;
    }

    private static string Rel(string vol, string p) => p.StartsWith(vol, StringComparison.OrdinalIgnoreCase) ? p[vol.Length..] : p;

    private static bool IsInside(string path, string baseDir)
    {
        var b = baseDir.TrimEnd('\\') + "\\";
        return path.StartsWith(b, StringComparison.OrdinalIgnoreCase) && path.Length > b.Length;
    }

    private IEnumerable<string> Expand(string dir, string[] segs, int i)
    {
        if (i == segs.Length) { yield return dir; yield break; }
        var seg = segs[i];
        if (seg == "**")
        {
            foreach (var r in Expand(dir, segs, i + 1)) yield return r;
            foreach (var sub in SubDirs(dir))
                foreach (var r in Expand(sub, segs, i)) yield return r;
        }
        else if (seg.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            foreach (var sub in SubDirs(dir))
                if (FileSystemName.MatchesSimpleExpression(seg, Path.GetFileName(sub)))
                    foreach (var r in Expand(sub, segs, i + 1)) yield return r;
        }
        else
        {
            var p = Path.Combine(dir, seg);
            var di = new DirectoryInfo(p);
            if (di.Exists && !di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                foreach (var r in Expand(p, segs, i + 1)) yield return r;
        }
    }

    private static IEnumerable<string> SubDirs(string dir)
    {
        IEnumerable<DirectoryInfo> items;
        try { items = new DirectoryInfo(dir).EnumerateDirectories("*", Opt).ToList(); }
        catch { yield break; }
        foreach (var d in items)
            if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield return d.FullName;
    }

    private bool Excluded(CleanupRule rule, string name) =>
        _rules.GlobalExclude.Any(m => FileSystemName.MatchesSimpleExpression(m, name)) ||
        rule.Exclude.Any(m => FileSystemName.MatchesSimpleExpression(m, name));

    private bool Included(CleanupRule rule, string name) =>
        rule.Include.Count == 0 || rule.Include.Any(m => FileSystemName.MatchesSimpleExpression(m, name));

    private bool OldEnough(CleanupRule rule, FileSystemInfo fi) =>
        rule.OlderThanDays <= 0 || (_now - fi.LastWriteTime).TotalDays >= rule.OlderThanDays;

    private void Apply(CleanupRule rule, string target, CleanupStats s)
    {
        switch (rule.Action)
        {
            case CleanupAction.DeleteFiles:
                foreach (var f in Files(target))
                    if (Included(rule, f.Name) && !Excluded(rule, f.Name) && OldEnough(rule, f))
                        DeleteFile(f, s);
                break;

            case CleanupAction.DeleteContents:
                CleanDir(rule, target, s, keepSelf: true);
                break;

            case CleanupAction.DeleteFolder:
                var di = new DirectoryInfo(target);
                if (Excluded(rule, di.Name) || !OldEnough(rule, di)) return;
                CleanDir(rule, target, s, keepSelf: false);
                break;
        }
    }

    /// <summary>Recursively deletes matching files; removes directories that became empty.</summary>
    private bool CleanDir(CleanupRule rule, string dir, CleanupStats s, bool keepSelf)
    {
        _ct.ThrowIfCancellationRequested();
        bool allGone = true;
        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Opt).ToList(); }
        catch { s.Errors++; return false; }

        foreach (var e in entries)
        {
            if (Excluded(rule, e.Name)) { allGone = false; continue; }

            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // junction / symlink: remove the link itself, never what it points to
                if (rule.Include.Count == 0 && rule.OlderThanDays <= 0) { if (!DeleteEntry(e, s)) allGone = false; }
                else allGone = false;
                continue;
            }

            if (e is DirectoryInfo d)
            {
                if (!CleanDir(rule, d.FullName, s, keepSelf: false)) allGone = false;
            }
            else if (e is FileInfo f)
            {
                if (Included(rule, f.Name) && OldEnough(rule, f)) { if (!DeleteFile(f, s)) allGone = false; }
                else allGone = false;
            }
        }

        if (keepSelf || !allGone) return false;
        return DeleteEntry(new DirectoryInfo(dir), s);
    }

    private IEnumerable<FileInfo> Files(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", Opt).Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList(); }
        catch { return Array.Empty<FileInfo>(); }
    }

    private bool DeleteFile(FileInfo f, CleanupStats s)
    {
        long len = 0;
        try { len = f.Length; } catch { }
        if (!DeleteEntry(f, s)) return false;
        s.Bytes += len; s.Files++;
        _liveBytes += len; _liveFiles++;
        Tick(); // throttled inside the reporter
        return true;
    }

    private bool DeleteEntry(FileSystemInfo e, CleanupStats s)
    {
        if (_dryRun) { if (e is DirectoryInfo) s.Folders++; return true; }
        try
        {
            if ((e.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                e.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);
            if (e is DirectoryInfo d) d.Delete(false); else e.Delete();
        }
        catch (Exception) when (BackupDelete(e.FullName, e is DirectoryInfo)) { }
        catch (Exception ex)
        {
            s.Errors++;
            if (s.Errors <= 20) Log.Warn(Loc.T("Ops.Cleanup.NotDeleted", e.FullName, ex.Message));
            return false;
        }
        if (e is DirectoryInfo) s.Folders++;
        return true;
    }

    /// <summary>Delete using backup semantics (works through restrictive ACLs with SeRestorePrivilege).</summary>
    private static bool BackupDelete(string path, bool isDir)
    {
        using var h = NativeMethods.CreateFileW(@"\\?\" + path.TrimStart('\\', '?'), NativeMethods.DELETE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_DELETE_ON_CLOSE | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        return !h.IsInvalid;
    }
}
