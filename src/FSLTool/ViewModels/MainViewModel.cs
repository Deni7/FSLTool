using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using FSLTool.Models;
using FSLTool.Services;
using FSLTool.Views;
using Microsoft.Win32;

namespace FSLTool.ViewModels;

public enum ProfileFilter { All, Problem, Mounted, Orphan, Disabled, Stale, Compactable, Unmeasured }

public sealed class LogLine
{
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    private CleanupRuleSet _rules = new();
    private readonly Dictionary<string, long> _sizeCache;
    /// <summary>Persisted analysis results (loaded on the first scan, kept in sync after every operation).</summary>
    private Dictionary<string, AnalysisRecord>? _analysisCache;
    /// <summary>Daily size snapshots (loaded on the first scan, updated by every scan / refresh).</summary>
    private Dictionary<string, List<SizePoint>>? _history;
    private CancellationTokenSource? _cts;

    public ObservableCollection<PathEntry> Paths { get; }
    public BulkObservableCollection<ProfileInfo> Profiles { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<LogLine> LogLines { get; } = new();

    public MainViewModel()
    {
        Settings = Storage.LoadSettings();
        // Paths before ApplyThresholds: it re-colours the storage bars of every path
        Paths = new ObservableCollection<PathEntry>(Settings.ProfilePaths.Select(x => new PathEntry(x)));
        ApplyThresholds();
        _sizeCache = Storage.LoadSizeCache();

        View = CollectionViewSource.GetDefaultView(Profiles);
        View.Filter = o => o is ProfileInfo p && Matches(p);
        View.SortDescriptions.Add(new SortDescription(nameof(ProfileInfo.UsagePercent), ListSortDirection.Descending));

        Log.Entry += (t, lvl, msg) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Insert(0, new LogLine { Time = t, Level = lvl, Text = msg });
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });

        AddPathCommand = new RelayCommand(_ => AddPath());
        RemovePathCommand = new RelayCommand(p => RemovePath(p as PathEntry), p => p is PathEntry);
        RefreshCommand = new RelayCommand(_ => RefreshAsync(), _ => !IsScanning);
        RefreshVisibleCommand = new RelayCommand(_ => RefreshItemsAsync(View.Cast<ProfileInfo>().ToList(), "видимі контейнери"),
            _ => !IsScanning && !IsWorking && VisibleCount > 0);
        RefreshCheckedCommand = new RelayCommand(_ => RefreshItemsAsync(Profiles.Where(p => p.IsChecked).ToList(), "позначені контейнери"),
            _ => !IsScanning && !IsWorking && CheckedCount > 0);
        RefreshAllCommand = new RelayCommand(_ => RefreshItemsAsync(Profiles.ToList(), "усі контейнери"),
            _ => !IsScanning && !IsWorking && Profiles.Count > 0);
        RefreshOneCommand = new RelayCommand(p => RefreshItemsAsync(new[] { (ProfileInfo)p! }, ((ProfileInfo)p!).DisplayUser),
            p => p is ProfileInfo { IsBusy: false } && !IsScanning);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _cts != null);
        // clicking the active card again resets the filter
        SetFilterCommand = new RelayCommand(f =>
        {
            var target = Enum.Parse<ProfileFilter>((string)f!);
            Filter = target == Filter ? ProfileFilter.All : target;
        });

        AnalyzeCommand = new RelayCommand(_ => RunOnTargets("Аналіз", (p, pr, ct) => ProfileOperations.Analyze(p, _rules, pr, ct), confirm: null,
                parallel: AnalyzeParallel),
            _ => HasTargets && !IsWorking);
        // quick read-only measurement: exact usage + reclaimable space in seconds, without walking the folders
        MeasureCommand = new RelayCommand(_ => RunOnTargets("Вимірювання", (p, pr, ct) => ProfileOperations.Measure(p, pr, ct), confirm: null,
                parallel: MeasureParallel),
            _ => HasTargets && !IsWorking);
        CleanCommand = new RelayCommand(_ => RunOnTargets(Settings.CompactAfterCleanup ? "Очищення + стиснення" : "Очищення",
                (p, pr, ct) => ProfileOperations.Clean(p, _rules, Settings.CompactAfterCleanup, pr, ct),
                confirm: "Буде видалено дані за правилами очищення{0}.\nКонтейнери, що використовуються, буде пропущено.\n\nПродовжити?", confirmVerb: "Очистити"),
            _ => HasTargets && !IsWorking);
        CompactCommand = new RelayCommand(_ => RunOnTargets("Стиснення", (p, pr, ct) => ProfileOperations.Compact(p, pr, ct),
                confirm: "Стиснути {1} контейнер(и)? Під час операції користувач не зможе увійти.", confirmVerb: "Стиснути"),
            _ => HasTargets && !IsWorking);
        UnmountCommand = new RelayCommand(_ => UnmountAsync(), _ => Selected?.IsMounted == true && !IsWorking);
        GrowCommand = new RelayCommand(_ => GrowAsync(), _ => HasTargets && !IsWorking);
        RepairFsCommand = new RelayCommand(_ => RunOnTargets("Перевірка ФС", (p, pr, ct) => ProfileOperations.RepairFs(p, pr, ct),
                confirm: "Перевірити й виправити файлову систему{0} (аналог chkdsk /f)?\n\n" +
                         "Диск буде підключено для запису — поки триває перевірка, користувач не зможе увійти. " +
                         "Під час виправлення chkdsk може видалити записи, які неможливо відновити.",
                confirmVerb: "Перевірити"),
            _ => HasTargets && !IsWorking);
        MessageCommand = new RelayCommand(_ => MessageAsync(), _ => !IsWorking && SessionTargets().Count > 0);
        LogoffCommand = new RelayCommand(_ => LogoffAsync(), _ => !IsWorking && SessionTargets().Count > 0);
        // largest folders: click a row to drill in, crumbs / ← to go back up
        DrillCommand = new RelayCommand(x =>
        {
            if (Selected == null) return;
            switch (x)
            {
                case FolderUsage { CanDrill: true } u: Selected.OpenFolder(u.Node); break;
                case FolderCrumb c: Selected.OpenFolder(c.Node); break;
                case "up": Selected.OpenFolder(Selected.CurrentFolder?.Parent is { Parent: not null } up ? up : null); break;
            }
        });
        DeleteFileCommand = new RelayCommand(x => DeleteFileAsync((FileUsage)x!), x => CanDeleteFile(x as FileUsage));
        DeleteFolderCommand = new RelayCommand(x => DeleteFolderAsync((FolderUsage)x!),
            x => x is FolderUsage { CanDelete: true } && Selected is { IsMounted: false, IsBusy: false } && !IsWorking && !IsScanning);
        // enabled only when EVERY target is stale and not in use
        DeleteCommand = new RelayCommand(_ => DeleteAsync(), _ => !IsWorking && Targets() is { Count: > 0 } t && t.All(p => p.CanDelete));
        OpenFolderCommand = new RelayCommand(_ => ProfileOperations.OpenInExplorer(Selected!), _ => Selected != null);
        EditRulesCommand = new RelayCommand(_ => EditRules());
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);
        ReloadRulesCommand = new RelayCommand(_ => LoadRules(true));
        OpenLogsCommand = new RelayCommand(_ => { Directory.CreateDirectory(Storage.LogDir); Process.Start("explorer.exe", Storage.LogDir); });
        CheckAllCommand = new RelayCommand(v => { foreach (var p in View.Cast<ProfileInfo>()) p.IsChecked = Equals(v, "1"); NotifyTargets(); });
        SaveSettingsCommand = new RelayCommand(_ => { ApplyThresholds(); SaveSettings(); RefreshStats(); View.Refresh(); });

        LoadRules(false);
    }

    // ─────────────── commands ───────────────
    public RelayCommand AddPathCommand { get; }
    public RelayCommand RemovePathCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshVisibleCommand { get; }
    public RelayCommand RefreshCheckedCommand { get; }
    public RelayCommand RefreshAllCommand { get; }
    public RelayCommand RefreshOneCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand MeasureCommand { get; }

    /// <summary>Containers attached at once by the read-only batch operations (each is a separate virtual disk).</summary>
    private const int MeasureParallel = 4, AnalyzeParallel = 3;
    public RelayCommand CleanCommand { get; }
    public RelayCommand CompactCommand { get; }
    public RelayCommand UnmountCommand { get; }
    public RelayCommand GrowCommand { get; }
    public RelayCommand RepairFsCommand { get; }
    public RelayCommand MessageCommand { get; }
    public RelayCommand LogoffCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DrillCommand { get; }
    public RelayCommand DeleteFolderCommand { get; }
    public RelayCommand DeleteFileCommand { get; }

    /// <summary>A file from «Найбільші файли» may be deleted unless globalExclude protects it (NTUSER.DAT…).</summary>
    private bool CanDeleteFile(FileUsage? f) =>
        f != null && Selected is { IsMounted: false, IsBusy: false } && !IsWorking && !IsScanning &&
        !_rules.GlobalExclude.Any(m => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(m, f.Name));

    private async Task DeleteFileAsync(FileUsage f)
    {
        var p = Selected;
        if (p == null || !CanDeleteFile(f)) return;
        var rel = f.Path;
        var ext = System.IO.Path.GetExtension(f.Name).ToLowerInvariant();
        var note = ext switch
        {
            ".ost" => "Це кеш поштової скриньки Outlook: Outlook створить його заново й завантажить пошту з сервера (це може тривати довго й навантажити мережу).\n",
            ".pst" => "Увага: PST — це локальний архів пошти, його вміст може бути ЛИШЕ в цьому файлі.\n",
            _ => "",
        };
        var msg = $"Видалити файл з контейнера {p.DisplayUser}?\n\n" +
                  $"  {rel}\n  {f.SizeText}, змінено {f.ModifiedText} (за даними аналізу {p.AnalyzedText})\n\n" + note +
                  "Контейнер буде підключено для запису — поки триває операція, користувач не зможе увійти.\n" +
                  (Settings.CompactAfterCleanup ? "Після видалення VHDX буде стиснуто.\n" : "Стиснення вимкнено — файл VHDX не зменшиться, доки не натиснете «Стиснути».\n") +
                  "Видалені дані відновити неможливо.";
        if (!Dialog.Confirm("Видалення файлу", msg, "Видалити файл", kind: DialogKind.Danger)) return;

        await RunOnTargets("Видалення файлу",
            (pp, r, ct) => ProfileOperations.DeleteFileIn(pp, rel, _rules, Settings.CompactAfterCleanup, r, ct),
            confirm: null, explicitTargets: new[] { p });
        if (p.LastResult?.StartsWith("Помилка") == true) Dialog.Error("Видалення файлу", p.LastResult);
    }

    private async Task DeleteFolderAsync(FolderUsage u)
    {
        var p = Selected;
        if (p == null || u.Node == null || !u.CanDelete) return;
        var rel = u.Node.Path;
        var msg = $"Видалити папку з контейнера {p.DisplayUser}?\n\n" +
                  $"  {rel}\n  {u.SizeText} (за даними аналізу {p.AnalyzedText})\n\n" +
                  "Контейнер буде підключено для запису — поки триває операція, користувач не зможе увійти.\n" +
                  (Settings.CompactAfterCleanup ? "Після видалення VHDX буде стиснуто.\n" : "Стиснення вимкнено — файл VHDX не зменшиться, доки не натиснете «Стиснути».\n") +
                  "Видалені дані відновити неможливо.";
        if (!Dialog.Confirm("Видалення папки", msg, "Видалити папку", kind: DialogKind.Danger)) return;

        await RunOnTargets("Видалення папки",
            (pp, r, ct) => ProfileOperations.DeleteFolder(pp, rel, _rules, Settings.CompactAfterCleanup, r, ct),
            confirm: null, explicitTargets: new[] { p });
        if (p.LastResult?.StartsWith("Помилка") == true) Dialog.Error("Видалення папки", p.LastResult);
    }

    /// <summary>
    /// The Delete button is shown when at least one target meets a deletion criterion
    /// (inactive, or account disabled / expired in AD); enabled only when ALL targets may be deleted.
    /// </summary>
    public bool ShowDelete => Targets().Any(p => p.IsDeletable);
    /// <summary>Shown but disabled — explain why under the button.</summary>
    public bool DeleteBlocked => ShowDelete && !Targets().All(p => p.CanDelete);
    public string DeleteLabel => Targets() is { Count: > 1 } t ? $"Видалити профілі ({t.Count})" : "Видалити профіль";
    public string DeleteHint
    {
        get
        {
            var t = Targets();
            int bad = t.Count(p => !p.CanDelete);
            if (bad == 0) return "Остаточно видалити контейнер разом із папкою користувача";
            if (t.Count == 1)
                return t[0].IsMounted ? "Контейнер зараз використовується" : t[0].IsBusy ? "З контейнером зараз виконується операція" : "";
            int notEligible = t.Count(p => !p.IsDeletable), inUse = t.Count(p => p.IsDeletable && (p.IsMounted || p.IsBusy));
            return $"Кнопка активна, лише якщо всі позначені можна видалити (неактивні або вимкнені в AD, і не використовуються): " +
                   $"не відповідають {bad} з {t.Count}" +
                   (notEligible > 0 ? $" — активні облікові записи, що змінювались нещодавно: {notEligible}" : "") +
                   (inUse > 0 ? $"{(notEligible > 0 ? "," : " —")} зараз використовуються: {inUse}" : "");
        }
    }

    /// <summary>Re-evaluate everything that depends on the current selection / checked set.</summary>
    private void NotifyTargets()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ShowDelete));
        OnPropertyChanged(nameof(DeleteBlocked));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(DeleteHint));
    }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand EditRulesCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ReloadRulesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    // ─────────────── state ───────────────
    private ProfileInfo? _selected;
    public ProfileInfo? Selected { get => _selected; set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); NotifyTargets(); } } }
    public bool HasSelection => Selected != null;

    /// <summary>Left panel collapsed to a narrow strip; remembered in settings.json.</summary>
    public bool IsSidebarCollapsed
    {
        get => Settings.SidebarCollapsed;
        set
        {
            if (Settings.SidebarCollapsed == value) return;
            Settings.SidebarCollapsed = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set { if (Set(ref _isScanning, value)) OnPropertyChanged(nameof(IsIdle)); } }

    private bool _isWorking;
    public bool IsWorking { get => _isWorking; set { if (Set(ref _isWorking, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsScanning && !IsWorking;

    private string _status = "Готово";
    public string Status { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private ProfileFilter _filter = ProfileFilter.All;
    public ProfileFilter Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            View.Refresh();
            OnPropertyChanged(nameof(IsFiltered));
            OnPropertyChanged(nameof(FilterLabel));
            OnPropertyChanged(nameof(ShowUnmeasuredLink));
            OnPropertyChanged(nameof(ShowCompactableLink));
            OnPropertyChanged(nameof(VisibleCount));
        }
    }

    public bool IsFiltered => Filter != ProfileFilter.All;
    public string FilterLabel => Filter switch
    {
        ProfileFilter.Problem => "Переповнені та близькі до межі",
        ProfileFilter.Mounted => "Підмонтовані",
        ProfileFilter.Orphan => "Сироти",
        ProfileFilter.Disabled => "Вимкнені або прострочені облікові записи",
        ProfileFilter.Stale => "Неактивні",
        ProfileFilter.Compactable => $"Варто стиснути (≥ {Settings.CompactMinGb:0.#} GB)",
        ProfileFilter.Unmeasured => "Ще не виміряні (вільні) — позначте й натисніть «Виміряти»",
        _ => "",
    };
    public int VisibleCount => View.Cast<object>().Count();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) { View.Refresh(); OnPropertyChanged(nameof(VisibleCount)); } } }

    public string RulesSummary { get => _rulesSummary; private set => Set(ref _rulesSummary, value); }
    private string _rulesSummary = "";

    public int TotalCount => Profiles.Count;
    public string TotalSize => Format.Bytes(Profiles.Sum(p => p.FileSize));
    public int CriticalCount => Profiles.Count(p => p.Severity == Severity.Critical);
    public int WarningCount => Profiles.Count(p => p.Severity == Severity.Warning);
    public int MountedCount => Profiles.Count(p => p.IsMounted);
    public int OrphanCount => Profiles.Count(p => p.IsOrphan);
    public int StaleCount => Profiles.Count(p => p.IsStale && !p.IsMounted);
    public string OrphanSize => Format.Bytes(Profiles.Where(p => p.IsOrphan).Sum(p => p.FileSize));
    public int DisabledCount => Profiles.Count(p => p.IsDisabled);
    public string DisabledSize => Format.Bytes(Profiles.Where(p => p.IsDisabled).Sum(p => p.FileSize));
    public int CheckedCount => Profiles.Count(p => p.IsChecked);
    public int CompactableCount => Profiles.Count(p => p.WorthCompacting);
    public string CompactableSize => Format.Bytes(Profiles.Where(p => p.WorthCompacting).Sum(p => p.Reclaimable ?? 0));
    /// <summary>Containers without exact usage — the "worth compacting" card cannot judge them yet.</summary>
    public int UnmeasuredCount => Profiles.Count(IsUnmeasured);
    /// <summary>No exact usage yet and free right now (an in-use container cannot be measured).</summary>
    private static bool IsUnmeasured(ProfileInfo p) => !p.UsageIsExact && !p.IsMounted;
    public string CompactableHint => UnmeasuredCount == 0
        ? $"≈ {CompactableSize} повернеться"
        : $"≈ {CompactableSize} · не виміряно {UnmeasuredCount}";
    public string CompactableTip => UnmeasuredCount == 0
        ? "Проаналізовані контейнери, які стиснення зменшить щонайменше на поріг із налаштувань"
        : $"Враховано лише виміряні контейнери. Ще не виміряно: {UnmeasuredCount}.\n" +
          "Щоб їх виміряти: клік по картці → «Ще не виміряні» → «Позначити видимі» → «Виміряти» (кілька секунд на контейнер, лише читання).";

    /// <summary>Link in the filter bar: from "worth compacting" to the containers that still have to be measured.</summary>
    public bool ShowUnmeasuredLink => Filter == ProfileFilter.Compactable && UnmeasuredCount > 0;
    /// <summary>…and back.</summary>
    public bool ShowCompactableLink => Filter == ProfileFilter.Unmeasured;


    private bool HasTargets => Profiles.Any(p => p.IsChecked) || Selected != null;

    private bool Matches(ProfileInfo p)
    {
        if (Search.Length > 0 &&
            !(p.DisplayUser.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
              p.FilePath.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
              p.StateText.Contains(Search, StringComparison.OrdinalIgnoreCase)))
            return false;
        return Filter switch
        {
            ProfileFilter.Problem => p.Severity != Severity.Ok,
            ProfileFilter.Mounted => p.IsMounted,
            ProfileFilter.Orphan => p.IsOrphan,
            ProfileFilter.Disabled => p.IsDisabled,
            ProfileFilter.Stale => p.IsStale && !p.IsMounted,
            ProfileFilter.Compactable => p.WorthCompacting,
            ProfileFilter.Unmeasured => IsUnmeasured(p),
            _ => true,
        };
    }

    private void ApplyThresholds()
    {
        ProfileInfo.WarnPercent = Settings.WarnPercent;
        ProfileInfo.CriticalPercent = Settings.CriticalPercent;
        ProfileInfo.StaleDays = Settings.StaleDays;
        ProfileInfo.CompactMinBytes = (long)(Math.Max(0, Settings.CompactMinGb) * (1L << 30));
        OnPropertyChanged(nameof(FilterLabel));
        foreach (var p in Profiles) p.Recalc();
        foreach (var e in Paths) e.Notify();
    }

    public void SaveSettings()
    {
        Settings.ProfilePaths = Paths.Select(x => x.Path).ToList();
        try { Storage.SaveSettings(Settings); }
        catch (Exception ex) { Log.Error("Не вдалося зберегти налаштування: " + ex.Message); }
    }

    private void AddPath()
    {
        var dlg = new OpenFolderDialog { Title = "Папка з FSLogix-контейнерами", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FolderNames)
            if (!HasPath(f)) Paths.Add(new PathEntry(f));
        SaveSettings();
        _ = RefreshAsync();
    }

    /// <summary>Called from the UI when a path is typed/pasted (UNC paths can't always be picked in the dialog).</summary>
    public void AddPathText(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0 || HasPath(path)) return;
        Paths.Add(new PathEntry(path));
        SaveSettings();
        _ = RefreshAsync();
    }

    private bool HasPath(string path) => Paths.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private void RemovePath(PathEntry? p)
    {
        if (p == null) return;
        Paths.Remove(p);
        SaveSettings();
        _ = RefreshAsync();
    }

    private void LoadRules(bool verbose)
    {
        try
        {
            _rules = Storage.LoadRules(Settings);
            var on = _rules.Rules.Count(r => r.Enabled);
            RulesSummary = $"{on} з {_rules.Rules.Count} правил активні";
            if (verbose) Log.Info($"Правила перечитано: {Storage.RulesPath(Settings)} ({RulesSummary})");
        }
        catch (Exception ex)
        {
            RulesSummary = "Помилка у файлі правил!";
            Log.Error("Файл правил: " + ex.Message);
            if (verbose) Dialog.Error("Помилка у файлі правил", ex.Message);
        }
    }

    private void EditRules()
    {
        LoadRules(false);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Storage.RulesPath(Settings)}\"") { UseShellExecute = true });
    }

    // ─────────────── scanning ───────────────
    public async Task RefreshAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var selectedPath = Selected?.FilePath;
        try
        {
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();
            var timings = new List<string>();
            void Mark(string name) { timings.Add($"{name} {phase.Elapsed.TotalSeconds:0.0} с"); phase.Restart(); }

            // 1. directory listing only — the list is on screen after this
            Status = "Пошук контейнерів…";
            var roots = Paths.Select(x => x.Path).ToList();
            var list = await Task.Run(() => ProfileScanner.Discover(roots, _sizeCache, ct), ct);
            Mark("перелік файлів");
            _analysisCache ??= await Task.Run(Storage.LoadAnalysisCache, ct);
            _history ??= await Task.Run(Storage.LoadHistory, ct);
            ProfileStore.Attach(list, _history);   // growth column is filled right away; today's point is added at the end
            Mark("кеші");

            // keep analysis results / last known state of profiles we already know
            var old = Profiles.ToDictionary(p => p.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var p in list)
            {
                if (old.TryGetValue(p.FilePath, out var o))
                {
                    if (o.FileSize == p.FileSize) { p.VolumeTotal = o.VolumeTotal; p.VolumeUsed = o.VolumeUsed; p.AnalyzedAt = o.AnalyzedAt; p.FsDirty = o.FsDirty; }
                    p.SetFolderTree(o.FolderTree); p.Estimates = o.Estimates; p.TopFiles = o.TopFiles; p.LastResult = o.LastResult;
                    p.AccountName = o.AccountName; p.IsOrphan = o.IsOrphan;
                    p.SetHandles(o.Handles); p.State = o.State;
                }
                else p.State = MountState.Unknown; // not "Free" until we actually know
                // exact usage / folders / files from a previous run (any admin), if the file has not changed since
                if (p.AnalyzedAt == null && _analysisCache != null &&
                    _analysisCache.TryGetValue(p.FilePath, out var rec) && rec.FileSize == p.FileSize)
                    p.ApplyAnalysis(rec);
                p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfileInfo.IsChecked)) NotifyTargets(); };
            }

            // one Reset notification → one refresh of the sorted/filtered view instead of one per row
            Profiles.ReplaceAll(list);
            Selected = Profiles.FirstOrDefault(p => p.FilePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            RefreshStats();
            Mark("відображення");

            // 2–5 are independent of each other and mostly WAIT (disk, SMB/WMI, DNS, domain controller),
            // so they run at the same time: the scan takes as long as the slowest one, not the sum.
            // Each touches different properties of the containers.
            //   2. max sizes from VHDX headers (cached per file — on rescans only new containers are read)
            //   3. who has what mounted (SMB open files + lock probe)
            //   4–5. SID → account (one batched lookup), then disabled / expired / last logon in AD (LDAP)
            Status = "Заголовки VHDX, стан підключень, облікові записи…";
            var pending = new List<string> { "заголовки", "стан підключень", "облікові записи", "AD" };
            void Done(string what)
            {
                pending.Remove(what);
                RefreshStats();
                View.Refresh();
                if (pending.Count > 0) Status = "Ще триває: " + string.Join(", ", pending) + "…";
            }
            async Task<double> Timed(Func<Task> work)
            {
                var sw = Stopwatch.StartNew();
                await work();
                return sw.Elapsed.TotalSeconds;
            }

            int read = 0;
            var tHeaders = Timed(async () =>
            {
                read = await Task.Run(() => ProfileScanner.ReadHeaders(list, _sizeCache, null, ct), ct);
                Storage.SaveSizeCache(_sizeCache);
                Done("заголовки");
            });
            var tStates = Timed(async () =>
            {
                await Task.Run(() => ProfileScanner.UpdateStates(list, ct), ct);
                Done("стан підключень");
            });
            double adSeconds = 0;
            var tAccounts = Timed(async () =>
            {
                await Task.Run(() => ProfileScanner.ResolveAccounts(list, ct), ct);
                Done("облікові записи");
                var sw = Stopwatch.StartNew();
                await Task.Run(() => AdService.Resolve(list, ct), ct);
                adSeconds = sw.Elapsed.TotalSeconds;
                Done("AD");
            });
            var secs = await Task.WhenAll(tHeaders, tStates, tAccounts);
            timings.Add($"паралельно {phase.Elapsed.TotalSeconds:0.0} с [заголовки ({read} нових) {secs[0]:0.0} с, " +
                        $"стан підключень {secs[1]:0.0} с, облікові записи {secs[2] - adSeconds:0.0} с, стан в AD {adSeconds:0.0} с]");
            phase.Restart();

            // 6. today's size → history (growth, forecast)
            Status = "Запис історії розмірів…";
            _history = await Task.Run(() => ProfileStore.RecordHistory(list), ct);
            View.Refresh();
            Mark("історія");

            var stamp = DateTime.Now;
            foreach (var p in list) p.RefreshedAt = stamp;
            await PruneAnalysisCacheAsync(roots, list);
            _ = RefreshStorageAsync();
            Status = $"Знайдено {list.Count} контейнерів за {total.Elapsed.TotalSeconds:0.0} с · {DateTime.Now:HH:mm:ss}";
            Log.Info($"Сканування: {list.Count} контейнерів, {TotalSize}, переповнених {CriticalCount}, підмонтованих {MountedCount}");
            Log.Info($"  час: {string.Join(", ", timings)}; усього {total.Elapsed.TotalSeconds:0.0} с");
        }
        catch (OperationCanceledException) { Status = "Скасовано"; }
        catch (Exception ex) { Status = "Помилка сканування"; Log.Error("Сканування: " + ex.Message); }
        finally
        {
            IsScanning = false;
            _cts = null;
        }
    }

    /// <summary>Re-reads all info (file, header, state, account) for the given containers only.</summary>
    public async Task RefreshItemsAsync(IReadOnlyList<ProfileInfo> list, string what)
    {
        if (list.Count == 0 || IsScanning) return;
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();
        bool single = list.Count == 1;
        foreach (var p in list) if (!p.IsBusy) p.Busy = "Оновлення…";
        try
        {
            Status = $"Оновлення: {what}…";
            await Task.Run(() => ProfileScanner.RefreshInfo(list.ToList(), _sizeCache,
                s => Status = $"Оновлення: {what} — {s}", ct), ct);
            Storage.SaveSizeCache(_sizeCache);
            _history = await Task.Run(() => ProfileStore.RecordHistory(list.Where(p => !p.IsDeleted).ToList()), ct);

            var gone = list.Where(p => p.IsDeleted).ToList();
            if (gone.Count > 0)
            {
                foreach (var p in gone) Log.Warn($"Контейнер зник з диска: {p.FilePath}");
                if (Selected is { IsDeleted: true }) Selected = null;
                Profiles.ReplaceAll(Profiles.Where(p => !p.IsDeleted).ToList());
            }
            RefreshStats();
            View.Refresh();
            Status = (single ? $"{list[0].DisplayUser}: інформацію оновлено" : $"Оновлено {list.Count - gone.Count} контейнерів") +
                     (gone.Count > 0 ? $", зникли з диска: {gone.Count}" : "") +
                     $" за {sw.Elapsed.TotalSeconds:0.0} с · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { Status = "Оновлення скасовано"; }
        catch (Exception ex) { Status = "Помилка оновлення"; Log.Error("Оновлення: " + ex.Message); }
        finally
        {
            foreach (var p in list) if (p.Busy == "Оновлення…") p.Busy = null;
            IsScanning = false;
            _cts = null;
        }
    }

    public void RefreshStats()
    {
        foreach (var n in new[] { nameof(TotalCount), nameof(TotalSize), nameof(CriticalCount), nameof(WarningCount),
                                  nameof(MountedCount), nameof(OrphanCount), nameof(StaleCount), nameof(OrphanSize), nameof(CheckedCount), nameof(VisibleCount),
                                  nameof(CompactableCount), nameof(CompactableSize), nameof(DisabledCount), nameof(DisabledSize),
                                  nameof(UnmeasuredCount), nameof(CompactableHint), nameof(CompactableTip), nameof(FilterLabel),
                                  nameof(ShowUnmeasuredLink), nameof(ShowCompactableLink) })
            OnPropertyChanged(n);
        NotifyTargets();   // stale/mounted state may have changed
    }

    // ─────────────── operations ───────────────
    private List<ProfileInfo> Targets()
    {
        var checkedOnes = Profiles.Where(p => p.IsChecked).ToList();
        return checkedOnes.Count > 0 ? checkedOnes : Selected != null ? new List<ProfileInfo> { Selected } : new();
    }

    /// <param name="parallel">
    /// How many containers are processed at once. Only for READ-ONLY operations (measure / analyse):
    /// each container is attached separately, so they do not interfere; writes stay one at a time.
    /// </param>
    private async Task RunOnTargets(string opName, Action<ProfileInfo, OpReporter, CancellationToken> op, string? confirm,
                                    string confirmVerb = "Так", IReadOnlyList<ProfileInfo>? explicitTargets = null, int parallel = 1)
    {
        var targets = explicitTargets?.ToList() ?? Targets();
        if (targets.Count == 0) return;

        var busy = targets.Where(t => t.IsMounted).ToList();
        if (confirm != null)
        {
            var who = targets.Count == 1 ? $" у контейнері {targets[0].DisplayUser}" : $" у {targets.Count} контейнерах";
            var msg = string.Format(confirm, who, targets.Count);
            if (busy.Count > 0) msg += $"\n\nУвага: {busy.Count} з них зараз підмонтовано — вони будуть пропущені.";
            if (!Dialog.Confirm(opName, msg, confirmVerb)) return;
        }

        IsWorking = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int ok = 0, failed = 0, skipped = 0, finished = 0, running = 0;
        var sw = Stopwatch.StartNew();
        bool sequential = parallel <= 1 || targets.Count == 1;

        // one container; false = the batch was cancelled. Continuations run on the UI thread, so the counters are safe.
        async Task<bool> Process(ProfileInfo p, int index)
        {
            if (ct.IsCancellationRequested) return false;
            running++;
            if (sequential)
            {
                Progress = 100.0 * index / targets.Count;
                Status = $"{opName}: {p.DisplayUser} ({index + 1}/{targets.Count})";
                // in a batch the details panel follows the container being processed
                if (Selected == null || targets.Contains(Selected)) Selected = p;
            }
            p.BeginBusy(opName);
            var reporter = new OpReporter(s =>
            {
                p.ApplyProgress(s);
                if (!sequential) return;
                Progress = 100.0 * (index + s.Overall) / targets.Count;
                Status = $"{opName}: {p.DisplayUser} ({index + 1}/{targets.Count}) · {s.Stage} · {s.Overall * 100:0}%";
            });
            try
            {
                await Task.Run(() => op(p, reporter, ct), ct);
                ok++;
            }
            catch (OperationCanceledException) { p.LastResult = "Скасовано"; return false; }
            catch (InvalidOperationException ex) when (p.IsMounted || ex is OperationSkippedException)
            {
                skipped++;
                p.LastResult = "Пропущено: " + ex.Message;
                Log.Warn($"{opName} {p.FilePath}: пропущено — {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                p.LastResult = "Помилка: " + ex.Message;
                Log.Error($"{opName} {p.FilePath}: {ex.Message}");
            }
            finally
            {
                p.EndBusy();
                running--;
                finished++;
                if (!sequential)
                {
                    Progress = 100.0 * finished / targets.Count;
                    Status = $"{opName}: {finished}/{targets.Count} готово, одночасно {running} · {sw.Elapsed:m\\:ss}";
                }
            }
            return true;
        }

        try
        {
            if (sequential)
            {
                for (int i = 0; i < targets.Count; i++)
                    if (!await Process(targets[i], i)) break;
            }
            else
            {
                Status = $"{opName}: 0/{targets.Count}, одночасно до {parallel}";
                using var gate = new SemaphoreSlim(parallel);
                await Task.WhenAll(targets.Select(async (p, i) =>
                {
                    await gate.WaitAsync();
                    try { await Process(p, i); }
                    finally { gate.Release(); }
                }));
            }
        }
        finally
        {
            Progress = 0;
            IsWorking = false;
            _cts = null;
            RefreshStats();
            View.Refresh();
            Status = $"{opName}: успішно {ok}" + (skipped > 0 ? $", пропущено {skipped}" : "") + (failed > 0 ? $", помилок {failed}" : "") +
                     $" · {sw.Elapsed:m\\:ss}";
            if (targets.Count > 1) Log.Info($"{opName}: {targets.Count} контейнерів, успішно {ok}, пропущено {skipped}, помилок {failed}, за {sw.Elapsed:m\\:ss}" +
                                            (sequential ? "" : $" (одночасно до {parallel})"));
        }
        await SaveAnalysisAsync(targets);
        _ = RefreshStorageAsync();
    }

    /// <summary>Free space of every configured root + what the containers under it take (runs in the background).</summary>
    private async Task RefreshStorageAsync()
    {
        var entries = Paths.ToList();
        if (entries.Count == 0) return;
        var spaces = await Task.Run(() => entries.AsParallel().AsOrdered().Select(e => e.Query()).ToList());
        for (int i = 0; i < entries.Count; i++)
        {
            var mine = Profiles.Where(p => p.RootPath.Equals(entries[i].Path, StringComparison.OrdinalIgnoreCase)).ToList();
            entries[i].Apply(spaces[i], mine.Count, mine.Sum(p => p.FileSize));
        }
    }

    /// <summary>Writes the current analysis state of these containers to the shared cache (or drops deleted ones).</summary>
    private async Task SaveAnalysisAsync(IReadOnlyList<ProfileInfo> targets)
    {
        try
        {
            var known = _analysisCache;
            var merged = await Task.Run(() => ProfileStore.SaveAnalysis(targets, known));
            if (merged != null) _analysisCache = merged;
        }
        catch (Exception ex) { Log.Warn("Кеш аналізу: " + ex.Message); }
    }

    /// <summary>
    /// After a full scan: drops cache entries for containers that are gone from the scanned roots
    /// (checked with File.Exists, so an unreachable share never wipes its entries).
    /// </summary>
    private async Task PruneAnalysisCacheAsync(IReadOnlyList<string> roots, IReadOnlyList<ProfileInfo> found)
    {
        if (_analysisCache is not { Count: > 0 } cache) return;
        var seen = found.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = cache.Keys.Where(k => !seen.Contains(k) &&
            roots.Any(r => k.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return;
        try
        {
            var gone = await Task.Run(() => candidates.Where(k => !File.Exists(k)).ToList());
            if (gone.Count == 0) return;
            _analysisCache = await Task.Run(() => Storage.UpdateAnalysisCache(d => { foreach (var k in gone) d.Remove(k); }));
        }
        catch (Exception ex) { Log.Warn("Кеш аналізу: " + ex.Message); }
    }

    private async Task DeleteAsync()
    {
        var targets = Targets();
        if (targets.Count == 0 || !targets.All(p => p.CanDelete)) return;

        var size = Format.Bytes(targets.Sum(p => p.FileSize));
        var names = string.Join("\n", targets.Take(10).Select(p =>
            $"  • {p.DisplayUser}  ({p.SizeText}; {p.DeleteReason}; змінювався {p.LastWrite:dd.MM.yyyy})"));
        if (targets.Count > 10) names += $"\n  … і ще {targets.Count - 10}";
        int recentDisabled = targets.Count(p => !p.IsStale);
        var msg = $"ОСТАТОЧНО видалити {targets.Count} профіл(ь/і) з диска разом із папками користувачів?\n\n{names}\n\n" +
                  $"Буде звільнено ≈ {size}. Відновити дані буде неможливо (кошика на мережевому ресурсі немає).\n" +
                  "Якщо в папці лишається інший контейнер (напр. ODFC), папку буде збережено — видалиться лише обраний файл." +
                  (recentDisabled > 0
                      ? $"\n\nУвага: {recentDisabled} з них змінювались нещодавно — їх дозволено видалити лише тому, що обліковий запис " +
                        "вимкнений або прострочений в AD. Перед видаленням стан в AD буде перевірено ще раз."
                      : "");
        if (!Dialog.Confirm("Видалення профілів", msg, "Видалити", kind: DialogKind.Danger)) return;
        if (targets.Count > 1 && !Dialog.Confirm("Видалення профілів", $"Підтвердіть ще раз: видалити {targets.Count} профілів ({size})?",
                $"Видалити {targets.Count}", kind: DialogKind.Danger))
            return;

        await RunOnTargets("Видалення", (p, r, ct) => ProfileOperations.Delete(p, Settings.StaleDays, r, ct), confirm: null);

        // drop deleted containers from the list and the size cache
        var gone = Profiles.Where(p => p.IsDeleted).ToList();
        if (gone.Count == 0) return;
        if (Selected is { IsDeleted: true }) Selected = null;
        lock (_sizeCache) foreach (var p in gone) _sizeCache.Remove(p.FilePath);
        Storage.SaveSizeCache(_sizeCache);
        var gonePaths = gone.Select(p => p.FilePath).ToList();
        await Task.Run(() => ProfileStore.ForgetHistory(gonePaths));
        Profiles.ReplaceAll(Profiles.Where(p => !p.IsDeleted).ToList());
        RefreshStats();
        Status = $"Видалено {gone.Count} з {targets.Count} профілів" + (gone.Count < targets.Count ? " — решту пропущено, див. журнал" : "");
    }

    private async Task UnmountAsync()
    {
        var p = Selected;
        if (p == null) return;
        var where = p.Handles.Count > 0
            ? string.Join("\n", p.Handles.Select(h => $"  • {h.ClientComputer}  {h.ClientUser}"))
            : "  • " + p.StateText;
        if (!Dialog.Confirm("Відмонтування",
                $"Примусово відмонтувати контейнер {p.DisplayUser}?\n\n{where}\n\n" +
                "Якщо користувач зараз у сесії, він втратить доступ до профілю, а незбережені дані можуть бути пошкоджені. " +
                "Рекомендується спершу завершити сеанс користувача кнопкою «Завершити сеанс».",
                "Відмонтувати", kind: DialogKind.Danger))
            return;

        IsWorking = true;
        p.Busy = "відмонтування…";
        try
        {
            await Task.Run(() => ProfileOperations.Unmount(p));
            Status = $"{p.DisplayUser}: {p.LastResult}";
        }
        catch (Exception ex)
        {
            p.LastResult = "Помилка: " + ex.Message;
            Log.Error($"Відмонтування {p.FilePath}: {ex.Message}");
            Dialog.Error("Відмонтування", ex.Message);
        }
        finally
        {
            p.Busy = null;
            IsWorking = false;
            RefreshStats();
        }
    }

    // ─────────────── grow (max size) ───────────────

    private async Task GrowAsync()
    {
        var targets = Targets();
        if (targets.Count == 0) return;

        var current = targets.Count == 1 && targets[0].VirtualSize is long v ? $"Зараз: {Format.Bytes(v)}.\n\n" : "";
        var input = Dialog.Prompt("Збільшення розміру",
            current + "Новий максимальний розмір у GB:\n" +
            "  • 50 — встановити 50 GB (контейнери, де вже стільки чи більше, буде пропущено);\n" +
            "  • +10 — додати 10 GB до поточного розміру кожного контейнера.",
            "+10", "Далі");
        if (input == null) return;

        var txt = input.Trim().Replace(',', '.').TrimEnd('G', 'g', 'B', 'b', ' ');
        bool relative = txt.StartsWith('+');
        if (!double.TryParse(txt.TrimStart('+'), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > 65536)
        {
            Dialog.Error("Збільшення розміру", $"Не вдалося розпізнати «{input}». Вкажіть число GB, наприклад 50 або +10.");
            return;
        }

        long bytes = (long)(gb * (1L << 30));
        var plan = new Dictionary<ProfileInfo, long>();
        foreach (var p in targets)
        {
            if (relative && p.VirtualSize == null) continue;   // unknown current size
            long target = relative ? p.VirtualSize!.Value + bytes : bytes;
            if (p.VirtualSize is long c && target <= c) continue;
            plan[p] = target;
        }
        if (plan.Count == 0)
        {
            Dialog.Info("Збільшення розміру", "Немає контейнерів, які потрібно збільшувати: поточний розмір уже не менший (або невідомий).");
            return;
        }

        var lines = string.Join("\n", plan.Take(10).Select(kv =>
            $"  • {kv.Key.DisplayUser}: {kv.Key.MaxSizeText} → {Format.Bytes(kv.Value)}"));
        if (plan.Count > 10) lines += $"\n  … і ще {plan.Count - 10}";
        var skipped = targets.Count - plan.Count;
        var storage = string.Join("\n", Paths.Where(e => plan.Keys.Any(p => p.RootPath.Equals(e.Path, StringComparison.OrdinalIgnoreCase)))
                                             .Select(e => $"  {e.Path}: {e.SpaceText}"));
        var msg = $"Збільшити максимальний розмір {plan.Count} контейнер(ів)?\n\n{lines}\n\n" +
                  (skipped > 0 ? $"Пропущено {skipped}: розмір уже не менший або невідомий.\n" : "") +
                  "Файл VHDX динамічний — місце на сховищі займатиметься поступово, у міру заповнення.\n" +
                  (storage.Length > 0 ? $"Сховище:\n{storage}\n" : "") +
                  "\nПоки триває операція, користувач не зможе увійти. Зменшити розмір назад цим інструментом не можна.";
        if (!Dialog.Confirm("Збільшення розміру", msg, "Збільшити", kind: DialogKind.Warning)) return;

        await RunOnTargets("Збільшення розміру", (p, r, ct) => ProfileOperations.Grow(p, plan[p], r, ct),
            confirm: null, explicitTargets: plan.Keys.ToList());

        lock (_sizeCache)
            foreach (var p in plan.Keys)
                if (p.VirtualSize is long vs) _sizeCache[p.FilePath] = vs;
        Storage.SaveSizeCache(_sizeCache);
    }

    // ─────────────── RDS sessions: message / logoff ───────────────

    /// <summary>Targets that are open over SMB by a known session host.</summary>
    private List<ProfileInfo> SessionTargets() => Targets().Where(p => p.HasSessionHost).ToList();

    /// <summary>Sessions of the container's user on the hosts that hold it (blocking).</summary>
    private static List<RdsSession> FindSessions(ProfileInfo p)
    {
        var accounts = new List<string?> { p.AccountName, p.UserName };
        accounts.AddRange(p.Handles.Select(h => h.ClientUser));
        var result = new List<RdsSession>();
        foreach (var host in p.Handles.Select(h => h.ClientComputer).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            result.AddRange(RdsService.FindSessions(host, accounts));
        return result;
    }

    private async Task MessageAsync()
    {
        var targets = SessionTargets();
        if (targets.Count == 0) return;
        var who = string.Join("\n", targets.Take(10).Select(p => $"  • {p.DisplayUser} @ {p.StateHost}"));
        if (targets.Count > 10) who += $"\n  … і ще {targets.Count - 10}";
        var text = Dialog.Prompt("Повідомлення користувачам",
            $"Повідомлення з'явиться в сеансі на RDS-хості:\n{who}",
            "Будь ласка, збережіть роботу й вийдіть із системи протягом 5 хвилин: профіль буде обслуговуватися.",
            "Надіслати", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;

        IsWorking = true;
        int sent = 0, failed = 0, missing = 0;
        try
        {
            foreach (var p in targets)
            {
                p.Busy = "надсилання повідомлення…";
                try
                {
                    var sessions = await Task.Run(() => FindSessions(p));
                    if (sessions.Count == 0) { missing++; p.LastResult = "Сеанс користувача на хості не знайдено"; continue; }
                    await Task.Run(() => { foreach (var s in sessions) RdsService.SendMessage(s, "Повідомлення від адміністратора", text); });
                    sent += sessions.Count;
                    p.LastResult = "Повідомлення надіслано: " + string.Join(", ", sessions);
                    Log.Info($"Повідомлення {p.DisplayUser} ({string.Join(", ", sessions)}): {text.ReplaceLineEndings(" ")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    p.LastResult = "Помилка надсилання: " + ex.Message;
                    Log.Error($"Повідомлення {p.DisplayUser}: {ex.Message}");
                }
                finally { p.Busy = null; }
            }
        }
        finally { IsWorking = false; }
        Status = $"Повідомлення: надіслано в {sent} сеанс(ів)" + (missing > 0 ? $", сеанс не знайдено: {missing}" : "") +
                 (failed > 0 ? $", помилок: {failed}" : "");
    }

    private async Task LogoffAsync()
    {
        var targets = SessionTargets();
        if (targets.Count == 0) return;

        IsWorking = true;
        var found = new List<(ProfileInfo p, List<RdsSession> sessions)>();
        try
        {
            Status = "Пошук сеансів на RDS-хостах…";
            foreach (var p in targets)
            {
                try { found.Add((p, await Task.Run(() => FindSessions(p)))); }
                catch (Exception ex)
                {
                    p.LastResult = "Не вдалося отримати сеанси: " + ex.Message;
                    Log.Error($"Сеанси {p.DisplayUser} @ {p.StateHost}: {ex.Message}");
                }
            }
        }
        finally { IsWorking = false; Status = "Готово"; }

        var withSessions = found.Where(f => f.sessions.Count > 0).ToList();
        var notFound = targets.Where(t => !withSessions.Any(f => f.p == t)).ToList();
        if (withSessions.Count == 0)
        {
            Dialog.Error("Завершення сеансу", "Сеансів цих користувачів на хостах не знайдено " +
                "(або немає доступу до RDS API хоста). Можна скористатися кнопкою «Відмонтувати».");
            return;
        }

        var list = string.Join("\n", withSessions.SelectMany(f => f.sessions).Take(12).Select(s => "  • " + s));
        var msg = $"Завершити сеанс(и) користувачів?\n\n{list}\n\n" +
                  (notFound.Count > 0 ? $"Для {notFound.Count} контейнер(ів) сеанс не знайдено — їх буде пропущено.\n\n" : "") +
                  "Незбережені документи в сеансі буде втрачено. FSLogix коректно відключить профіль під час виходу — " +
                  "це безпечніше, ніж «Відмонтувати». Рекомендується спершу надіслати повідомлення.";
        if (!Dialog.Confirm("Завершення сеансу", msg, "Завершити сеанс", kind: DialogKind.Danger)) return;

        IsWorking = true;
        int ok = 0, failed = 0;
        try
        {
            foreach (var (p, sessions) in withSessions)
            {
                p.Busy = "завершення сеансу…";
                try
                {
                    foreach (var s in sessions)
                    {
                        Status = $"Завершення сеансу: {s}";
                        Log.Warn($"Завершення сеансу {s} (контейнер {p.FilePath})");
                        await Task.Run(() => RdsService.Logoff(s));
                        ok++;
                    }
                    p.LastResult = "Сеанс завершено: " + string.Join(", ", sessions.Select(s => $"{s.Host} #{s.Id}"));
                }
                catch (Exception ex)
                {
                    failed++;
                    p.LastResult = "Помилка завершення сеансу: " + ex.Message;
                    Log.Error($"Завершення сеансу {p.DisplayUser}: {ex.Message}");
                }
                finally { p.Busy = null; }
            }
        }
        finally { IsWorking = false; }

        // the profile is detached during logoff; give FSLogix a moment, then re-read the state
        Status = "Очікування відключення профілів…";
        await Task.Delay(TimeSpan.FromSeconds(5));
        await RefreshItemsAsync(withSessions.Select(f => f.p).ToList(), "після завершення сеансів");
        Status = $"Завершено сеансів: {ok}" + (failed > 0 ? $", помилок: {failed}" : "") +
                 $"; ще підмонтовано: {withSessions.Count(f => f.p.IsMounted)}";
    }
}
