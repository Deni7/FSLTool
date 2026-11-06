using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using FSLTool.ViewModels;

namespace FSLTool.Models;

public enum ContainerType { Profile, ODFC, Unknown }

public enum MountState
{
    /// <summary>Not opened by anyone — safe to clean / compact.</summary>
    Free,
    /// <summary>Opened over SMB by a session host (user is logged on).</summary>
    InUseSmb,
    /// <summary>Attached as a virtual disk on this server.</summary>
    AttachedLocal,
    /// <summary>Opened by an unknown process (sharing violation).</summary>
    Locked,
    Unknown,
}

public enum Severity { Ok, Warning, Critical }

public sealed class SmbHandle
{
    public string FileId { get; init; } = "";
    public string ClientComputer { get; init; } = "";
    public string ClientUser { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public bool FromNetApi { get; init; }
}

/// <summary>
/// Directory size tree built during analysis. Only folders ≥ <see cref="Threshold"/> are kept as nodes
/// (with their own big subfolders); everything smaller is folded into SmallCount/SmallSize,
/// so even a profile with 100k folders costs only a handful of nodes.
/// </summary>
public sealed class FolderNode
{
    public static long Threshold { get; set; } = 100L << 20;   // 100 MB

    public string Name { get; init; } = "";
    /// <summary>Path relative to the container root, e.g. AppData\Local\Microsoft.</summary>
    public string Path { get; init; } = "";
    public long Size { get; set; }
    /// <summary>Files lying directly in this folder.</summary>
    public long FilesSize { get; set; }
    public List<FolderNode> Children { get; init; } = new();
    /// <summary>Subfolders below the threshold (not kept as nodes).</summary>
    public int SmallCount { get; set; }
    public long SmallSize { get; set; }
    [JsonIgnore] public FolderNode? Parent { get; set; }

    /// <summary>Restores <see cref="Parent"/> links after the tree was loaded from the analysis cache.</summary>
    public void LinkParents()
    {
        foreach (var c in Children) { c.Parent = this; c.LinkParents(); }
    }

    /// <summary>Folders whose removal would break the profile — never offered for deletion.</summary>
    private static readonly string[] Protected = { "", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow" };
    public static bool IsProtected(string relPath) =>
        Protected.Any(x => x.Equals(relPath.Trim('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes this node from the tree and subtracts its size from all ancestors.</summary>
    public void Detach()
    {
        if (Parent == null) return;
        Parent.Children.Remove(this);
        for (var a = Parent; a != null; a = a.Parent) a.Size -= Size;
        Parent = null;
    }

    public FolderNode? Find(string relPath)
    {
        var n = this;
        foreach (var seg in relPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            n = n.Children.FirstOrDefault(c => c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (n == null) return null;
        }
        return n;
    }
}

/// <summary>One row of the "largest folders" list.</summary>
public sealed class FolderUsage
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public double Fraction { get; init; }
    public string SizeText => Format.Bytes(Size);
    /// <summary>Node to drill into; null for summary rows.</summary>
    public FolderNode? Node { get; init; }
    public bool CanDrill => Node is { Children.Count: > 0 };
    /// <summary>Muted summary line ("файли в папці", "дрібніші за 100 MB").</summary>
    public bool IsSummary { get; init; }
    /// <summary>Synthetic row (e.g. "AppData\Local\(інше)") — drillable, but it is not a real folder.</summary>
    public bool IsPseudo { get; init; }
    public bool CanDelete => Node != null && !IsSummary && !IsPseudo && !FolderNode.IsProtected(Node.Path);
}

public sealed record FolderCrumb(string Name, FolderNode? Node);

public sealed class RuleEstimate
{
    public string RuleName { get; init; } = "";
    /// <summary>Rule name in the UI language (RuleName is the stable name kept in the analysis cache).</summary>
    [JsonIgnore] public string DisplayName => RuleNames.Display(RuleName);
    public long Bytes { get; init; }
    public int Files { get; init; }
    [JsonIgnore] public string SizeText => Format.Bytes(Bytes);
}

/// <summary>One row of the "largest files" list (collected during analysis).</summary>
public sealed class FileUsage
{
    /// <summary>Only files at least this big are listed.</summary>
    public const long MinSize = 100L << 20;   // 100 MB — same as the folder threshold; smaller files are not worth deleting by hand
    public const int MaxCount = 20;

    /// <summary>Path relative to the container root, e.g. AppData\Local\Microsoft\Outlook\x.ost.</summary>
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } d ? d : Loc.T("Profile.Files.Root");
    [JsonIgnore] public string SizeText => Format.Bytes(Size);
    [JsonIgnore] public string ModifiedText => Modified.ToString("dd.MM.yyyy");
    /// <summary>Size relative to the largest file in the list (bar width).</summary>
    [JsonIgnore] public double Fraction { get; set; }
}

/// <summary>One daily snapshot in %ProgramData%\FSLTool\history.json (short names keep the file small).</summary>
public sealed class SizePoint
{
    [JsonPropertyName("d")] public DateOnly Day { get; set; }
    /// <summary>Container file size.</summary>
    [JsonPropertyName("s")] public long Size { get; set; }
    /// <summary>Used bytes inside the volume, if an analysis was valid at that moment.</summary>
    [JsonPropertyName("u")] public long? Used { get; set; }
}

/// <summary>
/// Analysis results persisted in %ProgramData%\FSLTool\analysis-cache.json, so exact usage, folders and files
/// survive a restart and are shared by all admins. Valid only while the container file size is unchanged.
/// </summary>
public sealed class AnalysisRecord
{
    public long FileSize { get; set; }
    public long VolumeUsed { get; set; }
    public long VolumeTotal { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public bool? FsDirty { get; set; }
    public List<RuleEstimate>? Estimates { get; set; }
    public List<FileUsage>? TopFiles { get; set; }
    public FolderNode? Tree { get; set; }
}

public sealed class ProfileInfo : ObservableObject
{
    public static double WarnPercent { get; set; } = 80;
    public static double CriticalPercent { get; set; } = 90;
    public static int StaleDays { get; set; } = 90;
    /// <summary>From how much reclaimable space a container is worth compacting.</summary>
    public static long CompactMinBytes { get; set; } = 1L << 30;

    public required string FilePath { get; init; }
    public required string RootPath { get; init; }
    public string FolderName => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(FilePath)) ?? "";
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public ContainerType ContainerType { get; init; }
    public string DiskFormat => FilePath.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ? "VHD" : "VHDX";
    public string? Sid { get; init; }
    public string UserName { get; init; } = "";

    private string? _accountName;
    /// <summary>DOMAIN\user resolved from SID (null while resolving).</summary>
    public string? AccountName { get => _accountName; set { if (Set(ref _accountName, value)) OnPropertyChanged(nameof(DisplayUser)); } }

    private bool _isOrphan;
    /// <summary>SID could not be resolved — the account was most likely deleted.</summary>
    public bool IsOrphan { get => _isOrphan; set { if (Set(ref _isOrphan, value)) OnPropertyChanged(nameof(FlagsText)); } }

    public string DisplayUser => string.IsNullOrEmpty(AccountName) ? UserName : AccountName!;

    // ───── Active Directory state of the account ─────

    private bool _isDisabled;
    /// <summary>The account still exists in AD but is disabled or expired.</summary>
    public bool IsDisabled { get => _isDisabled; private set { if (Set(ref _isDisabled, value)) OnPropertyChanged(nameof(FlagsText)); } }

    private string? _adStateText;
    /// <summary>"вимкнений" / "термін дії минув" / null.</summary>
    public string? AdStateText { get => _adStateText; private set { if (Set(ref _adStateText, value)) { OnPropertyChanged(nameof(FlagsText)); OnPropertyChanged(nameof(AdText)); } } }

    private DateTime? _lastLogon;
    /// <summary>lastLogonTimestamp from AD (replicated with up to ~14 days of lag).</summary>
    public DateTime? LastLogon { get => _lastLogon; private set { if (Set(ref _lastLogon, value)) OnPropertyChanged(nameof(AdText)); } }

    private bool _adKnown;
    public string AdText => !_adKnown ? "—"
        : LastLogon is DateTime t ? Loc.T("Profile.Ad.WithLogon", AdStateText ?? Loc.T("Profile.Ad.Active"), t)
        : Loc.T("Profile.Ad.NoLogon", AdStateText ?? Loc.T("Profile.Ad.Active"));

    public void SetAdState(bool known, bool disabled, string? reason, DateTime? lastLogon)
    {
        _adKnown = known;
        IsDisabled = known && disabled;
        AdStateText = known ? reason : null;
        LastLogon = known ? lastLogon : null;
        OnPropertyChanged(nameof(AdText));
    }

    private long _fileSize;
    public long FileSize { get => _fileSize; set { if (Set(ref _fileSize, value)) Recalc(); } }

    private long? _virtualSize;
    public long? VirtualSize { get => _virtualSize; set { if (Set(ref _virtualSize, value)) Recalc(); } }

    private long? _volumeUsed, _volumeTotal;
    /// <summary>Used bytes on the NTFS volume inside the container (known after analysis).</summary>
    public long? VolumeUsed { get => _volumeUsed; set { if (Set(ref _volumeUsed, value)) Recalc(); } }
    public long? VolumeTotal { get => _volumeTotal; set { if (Set(ref _volumeTotal, value)) Recalc(); } }

    private DateTime _lastWrite;
    public DateTime LastWrite { get => _lastWrite; set { if (Set(ref _lastWrite, value)) { OnPropertyChanged(nameof(IsStale)); OnPropertyChanged(nameof(FlagsText)); } } }
    public bool IsStale => (DateTime.Now - LastWrite).TotalDays > StaleDays;

    /// <summary>Stale and not in use — the only containers that may be deleted.</summary>
    public bool CanDelete => IsDeletable && !IsMounted && !IsBusy;

    /// <summary>Meets a deletion criterion: not modified for StaleDays, or the account is disabled / expired in AD.</summary>
    public bool IsDeletable => IsStale || IsDisabled;

    /// <summary>Why the container may be deleted (shown in the confirmation).</summary>
    public string DeleteReason => string.Join(", ", new[]
    {
        IsDisabled ? AdStateText : null,
        IsStale ? Loc.T("Profile.DeleteReason.Stale", (DateTime.Now - LastWrite).TotalDays) : null,
    }.Where(x => x != null));

    /// <summary>Set after the container was deleted from disk; the row is then removed from the list.</summary>
    public bool IsDeleted { get; set; }

    private DateTime? _refreshedAt;
    /// <summary>When size/date/max size/state/account were last read from disk, SMB and AD.</summary>
    public DateTime? RefreshedAt { get => _refreshedAt; set { if (Set(ref _refreshedAt, value)) OnPropertyChanged(nameof(RefreshedText)); } }
    public string RefreshedText => RefreshedAt is DateTime t ? t.ToString("dd.MM.yyyy HH:mm:ss") : "—";

    private DateTime? _analyzedAt;
    /// <summary>When the volume inside the container was last measured (Аналіз / Очищення).</summary>
    public DateTime? AnalyzedAt { get => _analyzedAt; set { if (Set(ref _analyzedAt, value)) OnPropertyChanged(nameof(AnalyzedText)); } }
    public string AnalyzedText => AnalyzedAt is DateTime t ? t.ToString("dd.MM.yyyy HH:mm:ss") : Loc.T("Profile.NotAnalyzed");

    private bool _hasDiffDisk;
    /// <summary>RW.VHDX / differencing disk present next to the container.</summary>
    public bool HasDiffDisk { get => _hasDiffDisk; set { if (Set(ref _hasDiffDisk, value)) OnPropertyChanged(nameof(FlagsText)); } }

    private MountState _state;
    public MountState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(IsMounted)); OnPropertyChanged(nameof(HasSessionHost)); } }
    }
    public bool IsMounted => State is MountState.InUseSmb or MountState.AttachedLocal or MountState.Locked;

    public IReadOnlyList<SmbHandle> Handles { get; private set; } = Array.Empty<SmbHandle>();

    public void SetHandles(IReadOnlyList<SmbHandle> handles)
    {
        Handles = handles;
        OnPropertyChanged(nameof(Handles));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateHost));
        OnPropertyChanged(nameof(MountedBy));
        OnPropertyChanged(nameof(HasSessionHost));
    }

    public string MountedBy => Handles.Count == 0 ? "—" :
        string.Join(", ", Handles.Select(h => $"{h.ClientUser} @ {h.ClientComputer}".Trim(' ', '@')).Distinct());

    public string VolumeText => UsageIsExact
        ? Loc.T("Profile.VolumeOf", Format.Bytes(VolumeUsed!.Value), Format.Bytes(VolumeTotal!.Value))
        : Loc.T("Profile.NeedsMeasure");

    public string StateLabel => State switch
    {
        MountState.Free => Loc.T("Profile.State.Free"),
        MountState.InUseSmb => Loc.T("Profile.State.InSession"),
        MountState.AttachedLocal => Loc.T("Profile.State.Local"),
        MountState.Locked => Loc.T("Profile.State.Locked"),
        _ => "—",
    };

    public string StateHost => string.Join(", ", Handles.Select(h => h.ClientComputer).Where(s => s.Length > 0).Distinct());

    public string StateText => State switch
    {
        MountState.Free => Loc.T("Profile.State.Free"),
        MountState.InUseSmb => Loc.T("Profile.StateText.Mounted", string.Join(", ", Handles.Select(h => h.ClientComputer).Where(s => s.Length > 0).Distinct())),
        MountState.AttachedLocal => Loc.T("Profile.StateText.AttachedLocal"),
        MountState.Locked => Loc.T("Profile.State.Locked"),
        _ => "—",
    };

    /// <summary>Usage in percent: volume-level if analysed, otherwise file size / max size.</summary>
    public double? UsagePercent { get; private set; }
    public bool UsageIsExact => VolumeUsed.HasValue && VolumeTotal is > 0;
    public Severity Severity { get; private set; }
    public string UsageText => UsagePercent is double p ? $"{p:0}%" + (UsageIsExact ? "" : "*") : "?";
    public string SizeText => Format.Bytes(FileSize);
    public string MaxSizeText => VirtualSize is long v ? Format.Bytes(v) : "?";

    /// <summary>
    /// Space a compaction can roughly give back to the server: file size − used bytes inside the volume.
    /// Known only after analysis; an upper bound (VHDX metadata and not-yet-trimmed blocks are included).
    /// </summary>
    public long? Reclaimable => UsageIsExact ? Math.Max(0, FileSize - VolumeUsed!.Value) : null;
    public bool WorthCompacting => Reclaimable >= CompactMinBytes;
    public string ReclaimableText => Reclaimable is long r ? "≈ " + Format.Bytes(r) : "—";
    public string ReclaimableDetail => Reclaimable is long r ? "≈ " + Format.Bytes(r) : Loc.T("Profile.NeedsMeasure");

    public string FlagsText
    {
        get
        {
            var f = new List<string>();
            if (IsOrphan) f.Add(Loc.T("Profile.Flag.Orphan"));
            if (IsDisabled && AdStateText != null) f.Add(AdStateText);
            if (IsStale) f.Add(Loc.T("Profile.Flag.Stale", StaleDays));
            if (HasDiffDisk) f.Add("RW-diff");
            if (FsDirty == true) f.Add(Loc.T("Profile.Flag.FsDirty"));
            return string.Join(" · ", f);
        }
    }

    private bool? _fsDirty;
    /// <summary>NTFS dirty bit inside the container (known after analysis / FS check).</summary>
    public bool? FsDirty
    {
        get => _fsDirty;
        set { if (Set(ref _fsDirty, value)) { OnPropertyChanged(nameof(FlagsText)); OnPropertyChanged(nameof(FsText)); } }
    }
    public string FsText => FsDirty switch
    {
        true => Loc.T("Profile.Fs.Dirty"),
        false => Loc.T("Profile.Fs.Clean"),
        _ => Loc.T("Profile.NeedsMeasure"),
    };

    /// <summary>Session hosts that hold this container over SMB (where a session can be messaged / logged off).</summary>
    public bool HasSessionHost => State == MountState.InUseSmb && Handles.Any(h => h.ClientComputer.Length > 0);

    private string? _busy;
    /// <summary>Text of an operation that is currently running on this profile.</summary>
    public string? Busy { get => _busy; set { _busyIsRefresh = false; if (Set(ref _busy, value)) OnPropertyChanged(nameof(IsBusy)); } }
    public bool IsBusy => _busy != null;

    /// <summary>Busy was set by <see cref="BeginRefreshBusy"/> and not overwritten since (any other Busy assignment clears it).</summary>
    private bool _busyIsRefresh;

    /// <summary>Marks the row as being refreshed ("Оновлення…").</summary>
    public void BeginRefreshBusy()
    {
        Busy = Loc.T("Profile.Busy.Refreshing");
        _busyIsRefresh = true;
    }

    /// <summary>Clears Busy only if it still shows the refresh started by <see cref="BeginRefreshBusy"/>.</summary>
    public void EndRefreshBusy()
    {
        if (_busyIsRefresh) Busy = null;
    }

    private string _busyOp = "";
    /// <summary>Operation name shown above the progress bar (Аналіз / Очищення / Стиснення).</summary>
    public string BusyOp { get => _busyOp; set => Set(ref _busyOp, value); }

    private double _busyPercent;
    public double BusyPercent { get => _busyPercent; private set { if (Set(ref _busyPercent, value)) OnPropertyChanged(nameof(BusyPercentText)); } }
    public string BusyPercentText => $"{BusyPercent:0}%";

    private string? _busyDetail;
    public string? BusyDetail { get => _busyDetail; private set => Set(ref _busyDetail, value); }

    private string _busyElapsed = "";
    public string BusyElapsed { get => _busyElapsed; private set => Set(ref _busyElapsed, value); }

    public void BeginBusy(string op)
    {
        BusyOp = op;
        BusyPercent = 0;
        BusyDetail = null;
        BusyElapsed = "0:00";
        Busy = Loc.T("Profile.Busy.Preparing");
    }

    public void ApplyProgress(FSLTool.Services.OpReporter.Snapshot s)
    {
        Busy = s.Stage;
        BusyPercent = s.Overall * 100;
        if (s.Detail != null) BusyDetail = s.Detail;
        BusyElapsed = s.Elapsed.ToString(s.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
    }

    public void EndBusy() { Busy = null; BusyDetail = null; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    // ───── largest folders: overview + drill-down over FolderTree ─────

    /// <summary>Size tree from the last analysis (null until analysed).</summary>
    public FolderNode? FolderTree { get; private set; }

    private FolderNode? _currentFolder;
    /// <summary>Folder being browsed; null = overview (AppData flattened to AppData\Local\Vendor).</summary>
    public FolderNode? CurrentFolder => _currentFolder;
    public bool IsDrilled => _currentFolder != null;

    private List<FolderUsage>? _topFolders;
    /// <summary>Rows shown for the current folder (only ≥ threshold + one "Інше" summary).</summary>
    public List<FolderUsage>? TopFolders { get => _topFolders; private set => Set(ref _topFolders, value); }

    private List<FolderCrumb> _crumbs = new();
    public List<FolderCrumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }

    /// <summary>After a folder was deleted inside the container: drop it from the tree and refresh the view.</summary>
    public void RemoveFolder(string relPath)
    {
        var node = FolderTree?.Find(relPath);
        if (node == null) return;
        // if we are browsing the deleted folder (or inside it), step out to its parent
        var cur = _currentFolder;
        for (var n = cur; n != null; n = n.Parent)
            if (n == node) { cur = node.Parent == FolderTree ? null : node.Parent; break; }
        node.Detach();
        OpenFolder(cur);
        if (TopFiles != null)
            TopFiles = TopFiles.Where(f => !f.Path.StartsWith(node.Path + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>After a file was deleted inside the container: drop it from the list and subtract its size from the folder tree.</summary>
    public void RemoveFile(string relPath, long size)
    {
        if (TopFiles != null)
            TopFiles = TopFiles.Where(f => !f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (FolderTree is not FolderNode root) return;

        var node = root;
        root.Size -= size;
        bool exact = true;
        foreach (var seg in (System.IO.Path.GetDirectoryName(relPath) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = node.Children.FirstOrDefault(c => c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (child == null) { exact = false; break; }   // the file sat in a folder folded into "smaller than threshold"
            node = child;
            node.Size -= size;
        }
        if (exact) node.FilesSize -= size;
        else node.SmallSize -= size;
        OpenFolder(_currentFolder);
    }

    private List<FileUsage>? _topFiles;
    /// <summary>Largest files from the last analysis (null until analysed or after cleanup).</summary>
    public List<FileUsage>? TopFiles
    {
        get => _topFiles;
        set
        {
            // older analyses (cache) were collected from 10 MB — show only what meets the current threshold
            var list = value?.Where(f => f.Size >= FileUsage.MinSize).ToList() is { Count: > 0 } l ? l : null;
            if (list != null)
            {
                long max = Math.Max(1, list.Max(f => f.Size));
                foreach (var f in list) f.Fraction = (double)f.Size / max;
            }
            Set(ref _topFiles, list);
        }
    }

    public void SetFolderTree(FolderNode? root)
    {
        FolderTree = root;
        OnPropertyChanged(nameof(FolderTree));
        OpenFolder(null);
    }

    /// <summary>Opens a folder of the tree (null = overview) and rebuilds the rows.</summary>
    public void OpenFolder(FolderNode? node)
    {
        _currentFolder = node;
        OnPropertyChanged(nameof(CurrentFolder));
        OnPropertyChanged(nameof(IsDrilled));
        if (FolderTree == null) { TopFolders = null; Crumbs = new(); return; }

        long thr = FolderNode.Threshold;
        var rows = new List<(string name, long size, FolderNode? node, bool pseudo)>();
        long total;
        if (node == null)
        {
            total = FolderTree.Size;
            foreach (var c in FolderTree.Children)
            {
                if (c.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                    foreach (var l2 in c.Children)
                    {
                        foreach (var l3 in l2.Children) rows.Add((l3.Path, l3.Size, l3, false));
                        // a big AppData\Local whose bulk is in small vendors / loose files
                        long rest = l2.Size - l2.Children.Sum(x => x.Size);
                        // display only: the row drills into l2 itself (IsPseudo), the text is never looked up
                        if (rest >= thr) rows.Add((Loc.T("Profile.Folders.Other", l2.Path), rest, l2, true));
                    }
                else rows.Add((c.Name, c.Size, c, false));
            }
            if (FolderTree.FilesSize >= thr) rows.Add((Loc.T("Profile.Folders.RootFiles"), FolderTree.FilesSize, null, true));
            Crumbs = new();
        }
        else
        {
            total = node.Size;
            foreach (var c in node.Children) rows.Add((c.Name, c.Size, c, false));
            var crumbs = new List<FolderCrumb>();
            for (var n = node; n != null && n != FolderTree; n = n.Parent) crumbs.Insert(0, new FolderCrumb(n.Name, n));
            crumbs.Insert(0, new FolderCrumb(Loc.T("Profile.Folders.Overview"), null));
            Crumbs = crumbs;
        }

        rows = rows.OrderByDescending(r => r.size).ToList();
        long max = Math.Max(1, rows.Select(r => r.size).DefaultIfEmpty(1).Max());
        var list = rows.Select(r => new FolderUsage { Name = r.name, Size = r.size, Fraction = (double)r.size / max, Node = r.node, IsPseudo = r.pseudo }).ToList();
        long other = total - rows.Sum(r => r.size);
        if (other > 0)
            list.Add(new FolderUsage { Name = Loc.T("Profile.Folders.Smaller", Format.Bytes(thr)), Size = other, IsSummary = true });
        TopFolders = list;
    }

    private List<RuleEstimate>? _estimates;
    public List<RuleEstimate>? Estimates { get => _estimates; set { if (Set(ref _estimates, value)) OnPropertyChanged(nameof(EstimateTotalText)); } }
    public string EstimateTotalText => Estimates == null ? "" : Format.Bytes(Estimates.Sum(e => e.Bytes));

    private string? _lastResult;
    /// <summary>Result of the last operation. Assigning it marks the result as NOT an error — use <see cref="SetError"/> for errors.</summary>
    public string? LastResult
    {
        get => _lastResult;
        set { Set(ref _lastResult, value); LastResultIsError = false; }
    }

    private bool _lastResultIsError;
    /// <summary>The last operation failed (<see cref="LastResult"/> holds the error text).</summary>
    public bool LastResultIsError { get => _lastResultIsError; private set => Set(ref _lastResultIsError, value); }

    /// <summary>Stores an error as the last result (sets <see cref="LastResultIsError"/>).</summary>
    public void SetError(string text)
    {
        LastResult = text;
        LastResultIsError = true;
    }

    public void RefreshState() { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(IsMounted)); }

    /// <summary>UI language changed: rebuild cached rows with translated text and re-read every bound property.</summary>
    public void OnLanguageChanged()
    {
        if (FolderTree != null) OpenFolder(_currentFolder);
        // new list instance so the "largest files" rows re-read their (translated) Folder text
        if (_topFiles != null) { _topFiles = _topFiles.ToList(); }
        // …and the "can be freed" rows their rule names
        if (_estimates != null) _estimates = _estimates.ToList();
        RefreshAllBindings();
    }

    /// <summary>Theme changed: colours of bars / chips come from converters — re-read every bound property.</summary>
    public void OnThemeChanged() => RefreshAllBindings();

    public void Recalc()
    {
        if (UsageIsExact)
            UsagePercent = 100.0 * VolumeUsed!.Value / VolumeTotal!.Value;
        else if (VirtualSize is > 0)
            UsagePercent = Math.Min(100.0, 100.0 * FileSize / VirtualSize.Value);
        else
            UsagePercent = null;

        Severity = UsagePercent switch
        {
            double p when p >= CriticalPercent => Severity.Critical,
            double p when p >= WarnPercent => Severity.Warning,
            _ => Severity.Ok,
        };
        OnPropertyChanged(nameof(UsagePercent));
        OnPropertyChanged(nameof(UsageIsExact));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(MaxSizeText));
        OnPropertyChanged(nameof(Reclaimable));
        OnPropertyChanged(nameof(WorthCompacting));
        OnPropertyChanged(nameof(ReclaimableText));
        OnPropertyChanged(nameof(ReclaimableDetail));
        RecalcGrowth();
    }

    // ───── size history: growth rate and when the limit is reached ─────

    /// <summary>Growth is measured over this many days (or the available history, if shorter).</summary>
    public const int GrowthWindowDays = 30;
    /// <summary>A forecast needs at least this much history.</summary>
    public const int MinForecastDays = 7;

    private IReadOnlyList<SizePoint>? _history;
    public IReadOnlyList<SizePoint>? History => _history;

    /// <summary>Daily snapshots of the file size (one per day, from scans and the headless mode).</summary>
    public void SetHistory(IReadOnlyList<SizePoint>? history)
    {
        _history = history;
        OnPropertyChanged(nameof(History));
        Spark = BuildSpark(history);
        RecalcGrowth();
    }

    /// <summary>Sum of day-to-day INCREASES of the file size in the window (compaction drops are ignored).</summary>
    public long? Growth { get; private set; }
    public int GrowthSpanDays { get; private set; }
    /// <summary>Bytes per day; used for sorting the column.</summary>
    public double? DailyGrowth { get; private set; }
    /// <summary>Days until usage reaches the "overfull" threshold at the current rate (≤ 365, else null).</summary>
    public int? DaysToLimit { get; private set; }

    public string GrowthText => Growth is long g ? (g > 0 ? "+" + Format.Bytes(g) : "0") : "—";
    public string GrowthSpanText => Growth == null ? Loc.T("Profile.Growth.NoHistory")
        : Loc.T("Profile.Growth.Span", GrowthSpanDays);
    public string EtaText => DaysToLimit switch
    {
        null => "",
        <= 0 => Loc.T("Profile.Eta.Reached"),
        int d => Loc.T("Profile.Eta.Days", d),
    };
    public bool EtaSoon => DaysToLimit is <= 30;

    private void RecalcGrowth()
    {
        Growth = null; DailyGrowth = null; DaysToLimit = null; GrowthSpanDays = 0;
        var h = _history;
        if (h is { Count: > 0 })
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var from = today.AddDays(-GrowthWindowDays);
            // the last point before the window is the baseline, then everything inside it, then "now"
            var series = new List<(DateOnly day, long size)>();
            var baseline = h.LastOrDefault(x => x.Day < from);
            if (baseline != null) series.Add((baseline.Day, baseline.Size));
            series.AddRange(h.Where(x => x.Day >= from).Select(x => (x.Day, x.Size)));
            if (series.Count > 0 && series[^1].day == today) series[^1] = (today, FileSize);
            else series.Add((today, FileSize));

            if (series.Count >= 2)
            {
                long g = 0;
                for (int i = 1; i < series.Count; i++) g += Math.Max(0, series[i].size - series[i - 1].size);
                int span = Math.Max(1, today.DayNumber - series[0].day.DayNumber);
                Growth = g;
                GrowthSpanDays = span;
                DailyGrowth = (double)g / span;

                long? limitOf = UsageIsExact ? VolumeTotal : VirtualSize;
                if (span >= MinForecastDays && DailyGrowth > 0 && limitOf is > 0)
                {
                    long usedNow = UsageIsExact ? VolumeUsed!.Value : FileSize;
                    double left = CriticalPercent / 100.0 * limitOf.Value - usedNow;
                    var days = (int)Math.Ceiling(left / DailyGrowth.Value);
                    if (days <= 365) DaysToLimit = Math.Max(0, days);
                }
            }
        }
        OnPropertyChanged(nameof(Growth));
        OnPropertyChanged(nameof(GrowthSpanDays));
        OnPropertyChanged(nameof(DailyGrowth));
        OnPropertyChanged(nameof(DaysToLimit));
        OnPropertyChanged(nameof(GrowthText));
        OnPropertyChanged(nameof(GrowthSpanText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(EtaSoon));
    }

    private System.Windows.Media.PointCollection? _spark;
    /// <summary>Points for the size sparkline in a fixed 300×48 box (the view scales the box uniformly).</summary>
    public System.Windows.Media.PointCollection? Spark { get => _spark; private set { if (Set(ref _spark, value)) OnPropertyChanged(nameof(SparkCaption)); } }
    public string SparkCaption => _history is { Count: >= 2 } h
        ? Loc.T("Profile.SparkCaption", h[0].Day, h[^1].Day, Format.Bytes(h.Min(x => x.Size)), Format.Bytes(h.Max(x => x.Size)))
        : "";

    private static System.Windows.Media.PointCollection? BuildSpark(IReadOnlyList<SizePoint>? h)
    {
        if (h is not { Count: >= 2 }) return null;
        const double W = 300, H = 48, Pad = 4;
        long max = h.Max(x => x.Size), min = h.Min(x => x.Size);
        int d0 = h[0].Day.DayNumber;
        double span = Math.Max(1, h[^1].Day.DayNumber - d0);
        var pc = new System.Windows.Media.PointCollection(h.Count);
        foreach (var x in h)
        {
            double y = max == min ? H / 2 : Pad + (double)(max - x.Size) / (max - min) * (H - 2 * Pad);   // flat → middle line
            pc.Add(new System.Windows.Point((x.Day.DayNumber - d0) / span * W, y));
        }
        pc.Freeze();
        return pc;
    }

    // ───── persisted analysis ─────

    /// <summary>Snapshot for the analysis cache; null if the volume was never measured.</summary>
    public AnalysisRecord? ToAnalysisRecord() => AnalyzedAt is DateTime at && UsageIsExact
        ? new AnalysisRecord
        {
            FileSize = FileSize, VolumeUsed = VolumeUsed!.Value, VolumeTotal = VolumeTotal!.Value, AnalyzedAt = at, FsDirty = FsDirty,
            Estimates = Estimates, TopFiles = TopFiles, Tree = FolderTree,
        }
        : null;

    /// <summary>Applies a cached analysis (caller checks that the file size still matches).</summary>
    public void ApplyAnalysis(AnalysisRecord r)
    {
        VolumeTotal = r.VolumeTotal;
        VolumeUsed = r.VolumeUsed;
        AnalyzedAt = r.AnalyzedAt;
        FsDirty = r.FsDirty;
        Estimates = r.Estimates;
        TopFiles = r.TopFiles;
        r.Tree?.LinkParents();
        SetFolderTree(r.Tree);
    }
}

public static class Format
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }
}
