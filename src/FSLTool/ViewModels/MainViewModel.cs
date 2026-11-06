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

/// <summary>Item of the language picker: Code = null means "follow the Windows language".</summary>
public sealed record LangOption(string? Code, string Name);

/// <summary>Item of the theme picker (Settings page).</summary>
public sealed record ThemeOption(AppTheme Mode, string Name);

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

        _customFilterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _customFilterTimer.Tick += (_, _) => { _customFilterTimer.Stop(); ApplyCustomFilter(); };
        _customFilter = new FilterGroup();
        _customFilter.Changed += OnCustomFilterChanged;
        ReloadSavedFilterNames();

        Log.Entry += (t, lvl, msg) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LogLines.Insert(0, new LogLine { Time = t, Level = lvl, Text = msg });
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
            // badge on the «Журнал» button: warnings / errors since the log was last opened
            if (!IsLogOpen && lvl is "WARN" or "ERROR") LogAlertCount++;
        });

        AddPathCommand = new RelayCommand(_ => AddPath());
        RemovePathCommand = new RelayCommand(p => RemovePath(p as PathEntry), p => p is PathEntry);
        RefreshCommand = new RelayCommand(_ => RefreshAsync(), _ => !IsScanning);
        RefreshVisibleCommand = new RelayCommand(_ => RefreshItemsAsync(View.Cast<ProfileInfo>().ToList(), Loc.T("Vm.Refresh.What.Visible")),
            _ => !IsScanning && !IsWorking && VisibleCount > 0);
        RefreshCheckedCommand = new RelayCommand(_ => RefreshItemsAsync(Profiles.Where(p => p.IsChecked).ToList(), Loc.T("Vm.Refresh.What.Checked")),
            _ => !IsScanning && !IsWorking && CheckedCount > 0);
        RefreshAllCommand = new RelayCommand(_ => RefreshItemsAsync(Profiles.ToList(), Loc.T("Vm.Refresh.What.All")),
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

        AnalyzeCommand = new RelayCommand(_ => RunOnTargets(Loc.T("Vm.Op.Analyze"), (p, pr, ct) => ProfileOperations.Analyze(p, _rules, pr, ct), confirm: null,
                parallel: AnalyzeParallel),
            _ => HasTargets && !IsWorking);
        // quick read-only measurement: exact usage + reclaimable space in seconds, without walking the folders
        MeasureCommand = new RelayCommand(_ => RunOnTargets(Loc.T("Vm.Op.Measure"), (p, pr, ct) => ProfileOperations.Measure(p, pr, ct), confirm: null,
                parallel: MeasureParallel),
            _ => HasTargets && !IsWorking);
        CleanCommand = new RelayCommand(_ => RunOnTargets(Loc.T(Settings.CompactAfterCleanup ? "Vm.Op.CleanCompact" : "Vm.Op.Clean"),
                (p, pr, ct) => ProfileOperations.Clean(p, _rules, Settings.CompactAfterCleanup, pr, ct),
                confirm: Loc.T("Vm.Clean.Confirm"), confirmVerb: Loc.T("Vm.Clean.Verb")),
            _ => HasTargets && !IsWorking);
        CompactCommand = new RelayCommand(_ => RunOnTargets(Loc.T("Vm.Op.Compact"), (p, pr, ct) => ProfileOperations.Compact(p, pr, ct),
                confirm: Loc.T("Vm.Compact.Confirm"), confirmVerb: Loc.T("Vm.Compact.Verb")),
            _ => HasTargets && !IsWorking);
        UnmountCommand = new RelayCommand(_ => UnmountAsync(), _ => Selected?.IsMounted == true && !IsWorking);
        GrowCommand = new RelayCommand(_ => GrowAsync(), _ => HasTargets && !IsWorking);
        RepairFsCommand = new RelayCommand(_ => RunOnTargets(Loc.T("Vm.Op.RepairFs"), (p, pr, ct) => ProfileOperations.RepairFs(p, pr, ct),
                confirm: Loc.T("Vm.RepairFs.Confirm"), confirmVerb: Loc.T("Vm.RepairFs.Verb")),
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
        ShowPageCommand = new RelayCommand(p => IsSettingsPage = Equals(p, "settings"));
        ToggleLogCommand = new RelayCommand(_ => IsLogOpen = !IsLogOpen);
        ToggleThemeCommand = new RelayCommand(_ => ThemeManager.Toggle());
        StepThresholdCommand = new RelayCommand(p => StepThreshold(p as string));
        ResetThresholdsCommand = new RelayCommand(_ => ResetThresholds());
        ReloadRulesCommand = new RelayCommand(_ => LoadRules(true));
        OpenLogsCommand = new RelayCommand(_ => { Directory.CreateDirectory(Storage.LogDir); Process.Start("explorer.exe", Storage.LogDir); });
        CheckAllCommand = new RelayCommand(v => { foreach (var p in View.Cast<ProfileInfo>()) p.IsChecked = Equals(v, "1"); NotifyTargets(); });
        SaveSettingsCommand = new RelayCommand(_ => { CommitThresholds(); ApplyThresholds(); SaveSettings(); RefreshStats(); View.Refresh(); });
        ClearCustomFilterCommand = new RelayCommand(_ => ResetCustomFilter());

        SetIdleStatus();
        _languages = BuildLanguages();
        Loc.Changed += OnLanguageChanged;
        ThemeManager.Changed += OnThemeChanged;
        LoadThresholdDrafts();

        LoadRules(false);
    }

    // ─────────────── UI language ───────────────

    private IReadOnlyList<LangOption> _languages;
    /// <summary>Language picker items: "System (…)" first, then every embedded translation.</summary>
    public IReadOnlyList<LangOption> Languages => _languages;

    /// <summary>Current choice (Code = null → follow Windows). Setting it switches the language and remembers it.</summary>
    public LangOption SelectedLanguage
    {
        get => _languages.FirstOrDefault(l => string.Equals(l.Code, Loc.Preference, StringComparison.OrdinalIgnoreCase)) ?? _languages[0];
        set
        {
            if (value == null || string.Equals(value.Code, Loc.Preference, StringComparison.OrdinalIgnoreCase)) return;
            Loc.SetLanguage(value.Code);
        }
    }

    private static IReadOnlyList<LangOption> BuildLanguages()
    {
        var sys = Loc.SystemLanguage;
        var sysName = Loc.Available.FirstOrDefault(l => l.Code.Equals(sys, StringComparison.OrdinalIgnoreCase))?.NativeName ?? sys;
        var list = new List<LangOption> { new(null, Loc.T("Lang.System", sysName)) };
        list.AddRange(Loc.Available.Select(l => new LangOption(l.Code, l.NativeName)));
        return list;
    }

    private void OnLanguageChanged()
    {
        // deferred: the change may come from inside the language combo box's SelectedItem setter
        var d = Application.Current?.Dispatcher;
        if (d == null) ApplyLanguage();
        else d.BeginInvoke(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        foreach (var p in Profiles) p.OnLanguageChanged();
        foreach (var e in Paths) e.Notify();
        _customFilter.OnLanguageChanged();
        _languages = BuildLanguages();
        _themeOptions = null;
        if (_statusIdle) SetIdleStatus();
        RefreshAllBindings();
        OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(SelectedTheme));
    }

    /// <summary>Short code of the UI language for the header pill (UK, EN…).</summary>
    public string LanguageCode => Loc.Lang.ToUpperInvariant();

    // ─────────────── theme ───────────────

    /// <summary>Theme picker: follow Windows / light / dark (names follow the UI language).</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions => _themeOptions ??= BuildThemeOptions();
    private IReadOnlyList<ThemeOption>? _themeOptions;
    private static IReadOnlyList<ThemeOption> BuildThemeOptions() => new[]
    {
        new ThemeOption(AppTheme.System, Loc.T("Theme.System")),
        new ThemeOption(AppTheme.Light, Loc.T("Theme.Light")),
        new ThemeOption(AppTheme.Dark, Loc.T("Theme.Dark")),
    };

    public ThemeOption SelectedTheme
    {
        get => ThemeOptions.First(o => o.Mode == ThemeManager.Preference);
        set { if (value != null && value.Mode != ThemeManager.Preference) ThemeManager.Set(value.Mode); }
    }

    public bool IsDark => ThemeManager.IsDark;

    /// <summary>Colours come from converters in a few places (bars, chips, avatars): make those bindings re-read them.</summary>
    private void OnThemeChanged()
    {
        foreach (var p in Profiles) p.OnThemeChanged();
        foreach (var e in Paths) e.Notify();
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(SelectedTheme));
    }

    // ─────────────── pages, log ───────────────

    private bool _isSettingsPage;
    /// <summary>Header tabs: false = «Контейнери», true = «Налаштування».</summary>
    public bool IsSettingsPage
    {
        get => _isSettingsPage;
        set
        {
            if (!Set(ref _isSettingsPage, value)) return;
            OnPropertyChanged(nameof(IsContainersPage));
            if (value) LoadThresholdDrafts();
        }
    }
    public bool IsContainersPage => !_isSettingsPage;

    private bool _isLogOpen;
    /// <summary>Log panel above the status bar (button «Журнал»); opening it resets the warning badge.</summary>
    public bool IsLogOpen
    {
        get => _isLogOpen;
        set { if (Set(ref _isLogOpen, value) && value) LogAlertCount = 0; }
    }

    private int _logAlertCount;
    public int LogAlertCount { get => _logAlertCount; private set => Set(ref _logAlertCount, value); }

    // ─────────────── thresholds (Settings page): edited as a draft, applied by «Застосувати й зберегти» ───────────────

    private double _draftWarn, _draftCrit, _draftCompactMin;
    private int _draftStale;
    public double DraftWarn { get => _draftWarn; set { if (Set(ref _draftWarn, value)) OnPropertyChanged(nameof(ThresholdsDirty)); } }
    public double DraftCrit { get => _draftCrit; set { if (Set(ref _draftCrit, value)) OnPropertyChanged(nameof(ThresholdsDirty)); } }
    public int DraftStale { get => _draftStale; set { if (Set(ref _draftStale, value)) OnPropertyChanged(nameof(ThresholdsDirty)); } }
    public double DraftCompactMin { get => _draftCompactMin; set { if (Set(ref _draftCompactMin, value)) OnPropertyChanged(nameof(ThresholdsDirty)); } }

    /// <summary>The draft differs from the saved thresholds.</summary>
    public bool ThresholdsDirty =>
        DraftWarn != Settings.WarnPercent || DraftCrit != Settings.CriticalPercent ||
        DraftStale != Settings.StaleDays || DraftCompactMin != Settings.CompactMinGb;

    private void LoadThresholdDrafts()
    {
        DraftWarn = Settings.WarnPercent;
        DraftCrit = Settings.CriticalPercent;
        DraftStale = Settings.StaleDays;
        DraftCompactMin = Settings.CompactMinGb;
        OnPropertyChanged(nameof(ThresholdsDirty));
    }

    /// <summary>Copies the draft into the settings, clamped to sensible ranges (1–100 %, ≥ 1 day, ≥ 0 GB).</summary>
    private void CommitThresholds()
    {
        Settings.WarnPercent = Math.Clamp(DraftWarn, 1, 100);
        Settings.CriticalPercent = Math.Clamp(DraftCrit, 1, 100);
        Settings.StaleDays = Math.Max(1, DraftStale);
        Settings.CompactMinGb = Math.Max(0, DraftCompactMin);
        LoadThresholdDrafts();
    }

    /// <summary>− / + next to a threshold: "warn:-1", "crit:+1", "stale:+1" (10 days), "compact:-1".</summary>
    private void StepThreshold(string? arg)
    {
        var parts = (arg ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var d)) return;
        switch (parts[0])
        {
            case "warn": DraftWarn = Math.Clamp(Math.Round(DraftWarn) + d, 1, 100); break;
            case "crit": DraftCrit = Math.Clamp(Math.Round(DraftCrit) + d, 1, 100); break;
            case "stale": DraftStale = Math.Max(1, DraftStale + d * 10); break;
            case "compact": DraftCompactMin = Math.Max(0, Math.Round(DraftCompactMin) + d); break;
        }
    }

    /// <summary>«Повернути рекомендовані»: the defaults of a fresh settings.json (not saved until «Застосувати»).</summary>
    private void ResetThresholds()
    {
        var d = new AppSettings();
        DraftWarn = d.WarnPercent;
        DraftCrit = d.CriticalPercent;
        DraftStale = d.StaleDays;
        DraftCompactMin = d.CompactMinGb;
    }

    // ─────────────── header / cards text ───────────────

    private DateTime? _lastScanAt;
    /// <summary>Line under the page title: "412 контейнерів · 3,8 TB · сканування о 09:42".</summary>
    public string HeroSummary => TotalCount == 0 ? Loc.T("Main.Hero.Empty")
        : Loc.T("Main.Hero.Summary", TotalCount, TotalSize) +
          (_lastScanAt is DateTime t ? Loc.T("Main.Hero.ScannedAt", t) : "");

    /// <summary>Header of the actions card: what the actions apply to.</summary>
    public string ActionsTarget => CheckedCount > 0 ? Loc.T("Main.Actions.ForChecked", CheckedCount) : Loc.T("Main.Actions.ForSelected");

    /// <summary>Rules card: enabled rules (names in the UI language), the first few as chips.</summary>
    public int RulesOn => _rulesOn;
    public int RulesTotal => _rulesTotal;
    /// <summary>The rules file could not be read (<see cref="RulesSummary"/> holds the message).</summary>
    public bool RulesError => _rulesError;
    public int GlobalExcludeCount => _rules.GlobalExclude.Count;
    public IReadOnlyList<string> RuleChips => _rules.Rules.Where(r => r.Enabled).Select(r => r.DisplayName).Take(6).ToList();
    public string RulesMore => _rulesOn > 6 ? Loc.T("Settings.Rules.More", _rulesOn - 6) : "";

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
    public RelayCommand ClearCustomFilterCommand { get; }
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
            ".ost" => Loc.T("Vm.DeleteFile.NoteOst"),
            ".pst" => Loc.T("Vm.DeleteFile.NotePst"),
            _ => "",
        };
        var msg = Loc.T("Vm.DeleteFile.Confirm", p.DisplayUser, rel, f.SizeText, f.ModifiedText, p.AnalyzedText, note, CompactNote());
        var title = Loc.T("Vm.DeleteFile.Title");
        if (!Dialog.Confirm(title, msg, Loc.T("Vm.DeleteFile.Verb"), kind: DialogKind.Danger)) return;

        await RunOnTargets(title,
            (pp, r, ct) => ProfileOperations.DeleteFileIn(pp, rel, _rules, Settings.CompactAfterCleanup, r, ct),
            confirm: null, explicitTargets: new[] { p });
        if (p.LastResultIsError && p.LastResult != null) Dialog.Error(title, p.LastResult);
    }

    /// <summary>Line of the delete confirmations: whether the VHDX will be compacted afterwards.</summary>
    private string CompactNote() => Loc.T(Settings.CompactAfterCleanup ? "Vm.Delete.CompactOn" : "Vm.Delete.CompactOff");

    private async Task DeleteFolderAsync(FolderUsage u)
    {
        var p = Selected;
        if (p == null || u.Node == null || !u.CanDelete) return;
        var rel = u.Node.Path;
        var msg = Loc.T("Vm.DeleteFolder.Confirm", p.DisplayUser, rel, u.SizeText, p.AnalyzedText, CompactNote());
        var title = Loc.T("Vm.DeleteFolder.Title");
        if (!Dialog.Confirm(title, msg, Loc.T("Vm.DeleteFolder.Verb"), kind: DialogKind.Danger)) return;

        await RunOnTargets(title,
            (pp, r, ct) => ProfileOperations.DeleteFolder(pp, rel, _rules, Settings.CompactAfterCleanup, r, ct),
            confirm: null, explicitTargets: new[] { p });
        if (p.LastResultIsError && p.LastResult != null) Dialog.Error(title, p.LastResult);
    }

    /// <summary>
    /// The Delete button is shown when at least one target meets a deletion criterion
    /// (inactive, or account disabled / expired in AD); enabled only when ALL targets may be deleted.
    /// </summary>
    public bool ShowDelete => Targets().Any(p => p.IsDeletable);
    /// <summary>Shown but disabled — explain why under the button.</summary>
    public bool DeleteBlocked => ShowDelete && !Targets().All(p => p.CanDelete);
    public string DeleteLabel => Targets() is { Count: > 1 } t ? Loc.T("Vm.Delete.LabelMany", t.Count) : Loc.T("Vm.Delete.LabelOne");
    public string DeleteHint
    {
        get
        {
            var t = Targets();
            int bad = t.Count(p => !p.CanDelete);
            if (bad == 0) return Loc.T("Vm.Delete.Hint.Ok");
            if (t.Count == 1)
                return t[0].IsMounted ? Loc.T("Vm.Delete.Hint.InUse") : t[0].IsBusy ? Loc.T("Vm.Delete.Hint.Busy") : "";
            int notEligible = t.Count(p => !p.IsDeletable), inUse = t.Count(p => p.IsDeletable && (p.IsMounted || p.IsBusy));
            return Loc.T("Vm.Delete.Hint.NotAll", bad, t.Count) +
                   (notEligible > 0 ? Loc.T("Vm.Delete.Hint.Active", notEligible) : "") +
                   (inUse > 0 ? Loc.T(notEligible > 0 ? "Vm.Delete.Hint.InUseNext" : "Vm.Delete.Hint.InUseFirst", inUse) : "");
        }
    }

    /// <summary>Re-evaluate everything that depends on the current selection / checked set.</summary>
    private void NotifyTargets()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ActionsTarget));
        OnPropertyChanged(nameof(ShowDelete));
        OnPropertyChanged(nameof(DeleteBlocked));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(DeleteHint));
    }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand EditRulesCommand { get; }
    public RelayCommand ShowPageCommand { get; }
    public RelayCommand ToggleLogCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand StepThresholdCommand { get; }
    public RelayCommand ResetThresholdsCommand { get; }
    public RelayCommand ReloadRulesCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CheckAllCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    // ─────────────── state ───────────────
    private ProfileInfo? _selected;
    public ProfileInfo? Selected { get => _selected; set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); NotifyTargets(); } } }
    public bool HasSelection => Selected != null;

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set { if (Set(ref _isScanning, value)) OnPropertyChanged(nameof(IsIdle)); } }

    private bool _isWorking;
    public bool IsWorking { get => _isWorking; set { if (Set(ref _isWorking, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !IsScanning && !IsWorking;

    private string _status = "";
    /// <summary>The status line shows the idle "ready" text (re-translated when the language changes).</summary>
    private bool _statusIdle;
    public string Status { get => _status; set { _statusIdle = false; Set(ref _status, value); } }

    private void SetIdleStatus()
    {
        Status = Loc.T("Vm.Status.Ready");
        _statusIdle = true;
    }

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
        ProfileFilter.Problem => Loc.T("Vm.Filter.Problem"),
        ProfileFilter.Mounted => Loc.T("Vm.Filter.Mounted"),
        ProfileFilter.Orphan => Loc.T("Vm.Filter.Orphan"),
        ProfileFilter.Disabled => Loc.T("Vm.Filter.Disabled"),
        ProfileFilter.Stale => Loc.T("Vm.Filter.Stale"),
        ProfileFilter.Compactable => Loc.T("Vm.Filter.Compactable", Settings.CompactMinGb),
        ProfileFilter.Unmeasured => Loc.T("Vm.Filter.Unmeasured"),
        _ => "",
    };
    public int VisibleCount => View.Cast<object>().Count();

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) { View.Refresh(); OnPropertyChanged(nameof(VisibleCount)); } } }

    /// <summary>"N з M правил активні" or the rules-file error; computed on access so it follows the UI language.</summary>
    public string RulesSummary => _rulesError ? Loc.T("Vm.Rules.ErrorSummary")
        : _rulesLoaded ? Loc.T("Vm.Rules.Summary", _rulesOn, _rulesTotal) : "";
    private bool _rulesLoaded, _rulesError;
    private int _rulesOn, _rulesTotal;

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
        ? Loc.T("Vm.Compactable.Hint", CompactableSize)
        : Loc.T("Vm.Compactable.HintUnmeasured", CompactableSize, UnmeasuredCount);
    public string CompactableTip => UnmeasuredCount == 0
        ? Loc.T("Vm.Compactable.Tip")
        : Loc.T("Vm.Compactable.TipUnmeasured", UnmeasuredCount);

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
        } && _customFilter.Matches(p);
    }

    // ─────────────── custom filter (button «Фільтр» next to the search box) ───────────────

    private readonly System.Windows.Threading.DispatcherTimer _customFilterTimer;
    private FilterGroup _customFilter;
    /// <summary>Condition tree edited in the filter window; combined (І) with the search and the stat-card filter.</summary>
    public FilterGroup CustomFilter
    {
        get => _customFilter;
        private set
        {
            _customFilter.Changed -= OnCustomFilterChanged;
            _customFilter = value;
            _customFilter.Changed += OnCustomFilterChanged;
            OnPropertyChanged();
            ApplyCustomFilter();
        }
    }

    public bool IsCustomFilterActive => _customFilter.IsActive;
    public int CustomFilterCount => _customFilter.ActiveConditionCount();
    /// <summary>Readable expression, e.g. «Заповнення, % ≥ 80 І (Стан підключення дорівнює У сесії АБО Сирота)».</summary>
    public string CustomFilterText => _customFilter.IsActive
        ? (CurrentSavedName != null ? Loc.T("Vm.CustomFilter.Named", CurrentSavedName, _customFilter.Describe()) : _customFilter.Describe())
        : Loc.T("Vm.CustomFilter.None");

    /// <summary>Name of the saved filter that was loaded / saved last (null = unnamed).</summary>
    public string? CurrentSavedName { get => _currentSavedName; private set { if (Set(ref _currentSavedName, value)) OnPropertyChanged(nameof(CustomFilterText)); } }
    private string? _currentSavedName;

    public ObservableCollection<string> SavedFilterNames { get; } = new();

    // typing in a value box changes the tree on every key stroke — re-filter once it settles
    private void OnCustomFilterChanged() { _customFilterTimer.Stop(); _customFilterTimer.Start(); }

    private void ApplyCustomFilter()
    {
        View.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(IsCustomFilterActive));
        OnPropertyChanged(nameof(CustomFilterCount));
        OnPropertyChanged(nameof(CustomFilterText));
    }

    public void ResetCustomFilter()
    {
        CurrentSavedName = null;
        CustomFilter = new FilterGroup();
    }

    private void ReloadSavedFilterNames(Dictionary<string, FilterGroup>? d = null)
    {
        d ??= Storage.LoadFilters();
        var names = d.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (names.SequenceEqual(SavedFilterNames)) return;   // keep the combo box selection
        SavedFilterNames.Clear();
        foreach (var n in names) SavedFilterNames.Add(n);
    }

    /// <summary>Loads a named filter (re-read from disk: another admin may have changed it).</summary>
    public void LoadSavedFilter(string name)
    {
        var d = Storage.LoadFilters();
        ReloadSavedFilterNames(d);
        if (!d.TryGetValue(name, out var g)) { Log.Warn(Loc.T("Vm.Filter.NotFound", name)); return; }
        CurrentSavedName = d.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        CustomFilter = g;
    }

    public void SaveCustomFilter(string name)
    {
        var copy = _customFilter.Clone();
        var d = Storage.UpdateFilters(x => { x.Remove(name); x[name] = copy; });
        ReloadSavedFilterNames(d);
        CurrentSavedName = name;
        Log.Info(Loc.T("Vm.Filter.Saved", name, copy.Describe()));
    }

    public void DeleteSavedFilter(string name)
    {
        var d = Storage.UpdateFilters(x => x.Remove(name));
        ReloadSavedFilterNames(d);
        if (string.Equals(CurrentSavedName, name, StringComparison.OrdinalIgnoreCase)) CurrentSavedName = null;
        Log.Info(Loc.T("Vm.Filter.Deleted", name));
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
        catch (Exception ex) { Log.Error(Loc.T("Vm.Settings.SaveFailed", ex.Message)); }
    }

    private void AddPath()
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Vm.AddPath.Title"), Multiselect = true };
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
            _rulesOn = _rules.Rules.Count(r => r.Enabled);
            _rulesTotal = _rules.Rules.Count;
            _rulesLoaded = true;
            _rulesError = false;
            OnPropertyChanged(nameof(RulesSummary));
            OnPropertyChanged(nameof(RulesOn));
            OnPropertyChanged(nameof(RulesTotal));
            OnPropertyChanged(nameof(RulesError));
            OnPropertyChanged(nameof(GlobalExcludeCount));
            OnPropertyChanged(nameof(RuleChips));
            OnPropertyChanged(nameof(RulesMore));
            if (verbose) Log.Info(Loc.T("Vm.Rules.Reloaded", Storage.RulesPath(Settings), RulesSummary));
        }
        catch (Exception ex)
        {
            _rulesError = true;
            OnPropertyChanged(nameof(RulesSummary));
            OnPropertyChanged(nameof(RulesError));
            Log.Error(Loc.T("Vm.Rules.ErrorLog", ex.Message));
            if (verbose) Dialog.Error(Loc.T("Vm.Rules.ErrorTitle"), ex.Message);
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
            void Mark(string key) { timings.Add(Loc.T("Vm.Scan.Timing", Loc.T(key), phase.Elapsed.TotalSeconds)); phase.Restart(); }

            // 1. directory listing only — the list is on screen after this
            Status = Loc.T("Vm.Scan.Searching");
            var roots = Paths.Select(x => x.Path).ToList();
            var list = await Task.Run(() => ProfileScanner.Discover(roots, _sizeCache, ct), ct);
            Mark("Vm.Scan.Phase.Listing");
            _analysisCache ??= await Task.Run(Storage.LoadAnalysisCache, ct);
            _history ??= await Task.Run(Storage.LoadHistory, ct);
            ProfileStore.Attach(list, _history);   // growth column is filled right away; today's point is added at the end
            Mark("Vm.Scan.Phase.Caches");

            // keep analysis results / last known state of profiles we already know
            var old = Profiles.ToDictionary(p => p.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var p in list)
            {
                if (old.TryGetValue(p.FilePath, out var o))
                {
                    if (o.FileSize == p.FileSize) { p.VolumeTotal = o.VolumeTotal; p.VolumeUsed = o.VolumeUsed; p.AnalyzedAt = o.AnalyzedAt; p.FsDirty = o.FsDirty; }
                    p.SetFolderTree(o.FolderTree); p.Estimates = o.Estimates; p.TopFiles = o.TopFiles;
                    if (o.LastResultIsError && o.LastResult != null) p.SetError(o.LastResult); else p.LastResult = o.LastResult;
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
            Mark("Vm.Scan.Phase.Display");

            // 2–5 are independent of each other and mostly WAIT (disk, SMB/WMI, DNS, domain controller),
            // so they run at the same time: the scan takes as long as the slowest one, not the sum.
            // Each touches different properties of the containers.
            //   2. max sizes from VHDX headers (cached per file — on rescans only new containers are read)
            //   3. who has what mounted (SMB open files + lock probe)
            //   4–5. SID → account (one batched lookup), then disabled / expired / last logon in AD (LDAP)
            Status = Loc.T("Vm.Scan.Parallel");
            // string keys, translated only for display (the language may change while the scan runs)
            const string kHeaders = "Vm.Scan.Pending.Headers", kStates = "Vm.Scan.Pending.States",
                         kAccounts = "Vm.Scan.Pending.Accounts", kAd = "Vm.Scan.Pending.Ad";
            var pending = new List<string> { kHeaders, kStates, kAccounts, kAd };
            void Done(string what)
            {
                pending.Remove(what);
                RefreshStats();
                View.Refresh();
                if (pending.Count > 0) Status = Loc.T("Vm.Scan.StillRunning", string.Join(", ", pending.Select(k => Loc.T(k))));
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
                Done(kHeaders);
            });
            var tStates = Timed(async () =>
            {
                await Task.Run(() => ProfileScanner.UpdateStates(list, ct), ct);
                Done(kStates);
            });
            double adSeconds = 0;
            var tAccounts = Timed(async () =>
            {
                await Task.Run(() => ProfileScanner.ResolveAccounts(list, ct), ct);
                Done(kAccounts);
                var sw = Stopwatch.StartNew();
                await Task.Run(() => AdService.Resolve(list, ct), ct);
                adSeconds = sw.Elapsed.TotalSeconds;
                Done(kAd);
            });
            var secs = await Task.WhenAll(tHeaders, tStates, tAccounts);
            timings.Add(Loc.T("Vm.Scan.ParallelTiming", phase.Elapsed.TotalSeconds, read, secs[0], secs[1], secs[2] - adSeconds, adSeconds));
            phase.Restart();

            // 6. today's size → history (growth, forecast)
            Status = Loc.T("Vm.Scan.History");
            _history = await Task.Run(() => ProfileStore.RecordHistory(list), ct);
            View.Refresh();
            Mark("Vm.Scan.Phase.History");

            var stamp = DateTime.Now;
            foreach (var p in list) p.RefreshedAt = stamp;
            await PruneAnalysisCacheAsync(roots, list);
            _ = RefreshStorageAsync();
            Status = Loc.T("Vm.Scan.Done", list.Count, total.Elapsed.TotalSeconds, DateTime.Now);
            _lastScanAt = DateTime.Now;
            OnPropertyChanged(nameof(HeroSummary));
            Log.Info(Loc.T("Vm.Scan.Log", list.Count, TotalSize, CriticalCount, MountedCount));
            Log.Info(Loc.T("Vm.Scan.LogTiming", string.Join(", ", timings), total.Elapsed.TotalSeconds));
        }
        catch (OperationCanceledException) { Status = Loc.T("Vm.Status.Cancelled"); }
        catch (Exception ex) { Status = Loc.T("Vm.Scan.Error"); Log.Error(Loc.T("Vm.Scan.ErrorLog", ex.Message)); }
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
        foreach (var p in list) if (!p.IsBusy) p.BeginRefreshBusy();
        try
        {
            Status = Loc.T("Vm.Refresh.Status", what);
            await Task.Run(() => ProfileScanner.RefreshInfo(list.ToList(), _sizeCache,
                s => Status = Loc.T("Vm.Refresh.Progress", what, s), ct), ct);
            Storage.SaveSizeCache(_sizeCache);
            _history = await Task.Run(() => ProfileStore.RecordHistory(list.Where(p => !p.IsDeleted).ToList()), ct);

            var gone = list.Where(p => p.IsDeleted).ToList();
            if (gone.Count > 0)
            {
                foreach (var p in gone) Log.Warn(Loc.T("Vm.Refresh.Gone", p.FilePath));
                if (Selected is { IsDeleted: true }) Selected = null;
                Profiles.ReplaceAll(Profiles.Where(p => !p.IsDeleted).ToList());
            }
            RefreshStats();
            View.Refresh();
            Status = (single ? Loc.T("Vm.Refresh.DoneOne", list[0].DisplayUser) : Loc.T("Vm.Refresh.DoneMany", list.Count - gone.Count)) +
                     (gone.Count > 0 ? Loc.T("Vm.Refresh.GoneSuffix", gone.Count) : "") +
                     Loc.T("Vm.Refresh.TimeSuffix", sw.Elapsed.TotalSeconds, DateTime.Now);
        }
        catch (OperationCanceledException) { Status = Loc.T("Vm.Refresh.Cancelled"); }
        catch (Exception ex) { Status = Loc.T("Vm.Refresh.Error"); Log.Error(Loc.T("Vm.Refresh.ErrorLog", ex.Message)); }
        finally
        {
            foreach (var p in list) p.EndRefreshBusy();
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
                                  nameof(ShowUnmeasuredLink), nameof(ShowCompactableLink), nameof(HeroSummary), nameof(ActionsTarget) })
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
                                    string? confirmVerb = null, IReadOnlyList<ProfileInfo>? explicitTargets = null, int parallel = 1)
    {
        var targets = explicitTargets?.ToList() ?? Targets();
        if (targets.Count == 0) return;

        var busy = targets.Where(t => t.IsMounted).ToList();
        if (confirm != null)
        {
            var who = targets.Count == 1 ? Loc.T("Vm.Confirm.WhoOne", targets[0].DisplayUser) : Loc.T("Vm.Confirm.WhoMany", targets.Count);
            var msg = string.Format(confirm, who, targets.Count);
            if (busy.Count > 0) msg += Loc.T("Vm.Confirm.MountedSkipped", busy.Count);
            if (!Dialog.Confirm(opName, msg, confirmVerb ?? Loc.T("Common.Yes"))) return;
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
            catch (OperationCanceledException) { p.LastResult = Loc.T("Vm.Status.Cancelled"); return false; }
            catch (InvalidOperationException ex) when (p.IsMounted || ex is OperationSkippedException)
            {
                skipped++;
                p.LastResult = Loc.T("Vm.Run.Skipped", ex.Message);
                Log.Warn(Loc.T("Vm.Run.SkippedLog", opName, p.FilePath, ex.Message));
            }
            catch (Exception ex)
            {
                failed++;
                p.SetError(Loc.T("Vm.Run.Error", ex.Message));
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
                    Status = Loc.T("Vm.Run.ParallelProgress", opName, finished, targets.Count, running, sw.Elapsed.ToString(@"m\:ss"));
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
                Status = Loc.T("Vm.Run.ParallelStart", opName, targets.Count, parallel);
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
            var elapsed = sw.Elapsed.ToString(@"m\:ss");
            Status = Loc.T("Vm.Run.Summary", opName, ok) + (skipped > 0 ? Loc.T("Vm.Run.SummarySkipped", skipped) : "") +
                     (failed > 0 ? Loc.T("Vm.Run.SummaryFailed", failed) : "") + $" · {elapsed}";
            if (targets.Count > 1) Log.Info(Loc.T("Vm.Run.Log", opName, targets.Count, ok, skipped, failed, elapsed) +
                                            (sequential ? "" : Loc.T("Vm.Run.LogParallel", parallel)));
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
        catch (Exception ex) { Log.Warn(Loc.T("Vm.AnalysisCache.Error", ex.Message)); }
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
        catch (Exception ex) { Log.Warn(Loc.T("Vm.AnalysisCache.Error", ex.Message)); }
    }

    private async Task DeleteAsync()
    {
        var targets = Targets();
        if (targets.Count == 0 || !targets.All(p => p.CanDelete)) return;

        var size = Format.Bytes(targets.Sum(p => p.FileSize));
        var names = string.Join("\n", targets.Take(10).Select(p =>
            Loc.T("Vm.Delete.Item", p.DisplayUser, p.SizeText, p.DeleteReason, p.LastWrite)));
        if (targets.Count > 10) names += Loc.T("Vm.More", targets.Count - 10);
        int recentDisabled = targets.Count(p => !p.IsStale);
        var msg = Loc.T("Vm.Delete.Confirm", targets.Count, names, size) +
                  (recentDisabled > 0 ? Loc.T("Vm.Delete.RecentWarning", recentDisabled) : "");
        var title = Loc.T("Vm.Delete.Title");
        if (!Dialog.Confirm(title, msg, Loc.T("Vm.Delete.Verb"), kind: DialogKind.Danger)) return;
        if (targets.Count > 1 && !Dialog.Confirm(title, Loc.T("Vm.Delete.Confirm2", targets.Count, size),
                Loc.T("Vm.Delete.Verb2", targets.Count), kind: DialogKind.Danger))
            return;

        await RunOnTargets(Loc.T("Vm.Op.Delete"), (p, r, ct) => ProfileOperations.Delete(p, Settings.StaleDays, r, ct), confirm: null);

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
        Status = Loc.T("Vm.Delete.Done", gone.Count, targets.Count) + (gone.Count < targets.Count ? Loc.T("Vm.Delete.DoneSkipped") : "");
    }

    private async Task UnmountAsync()
    {
        var p = Selected;
        if (p == null) return;
        var where = p.Handles.Count > 0
            ? string.Join("\n", p.Handles.Select(h => $"  • {h.ClientComputer}  {h.ClientUser}"))
            : "  • " + p.StateText;
        var title = Loc.T("Vm.Unmount.Title");
        if (!Dialog.Confirm(title, Loc.T("Vm.Unmount.Confirm", p.DisplayUser, where),
                Loc.T("Vm.Unmount.Verb"), kind: DialogKind.Danger))
            return;

        IsWorking = true;
        p.Busy = Loc.T("Vm.Unmount.Busy");
        try
        {
            await Task.Run(() => ProfileOperations.Unmount(p));
            Status = $"{p.DisplayUser}: {p.LastResult}";
        }
        catch (Exception ex)
        {
            p.SetError(Loc.T("Vm.Run.Error", ex.Message));
            Log.Error(Loc.T("Vm.Unmount.ErrorLog", p.FilePath, ex.Message));
            Dialog.Error(title, ex.Message);
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

        var title = Loc.T("Vm.Grow.Title");
        var current = targets.Count == 1 && targets[0].VirtualSize is long v ? Loc.T("Vm.Grow.Current", Format.Bytes(v)) : "";
        var input = Dialog.Prompt(title, current + Loc.T("Vm.Grow.Prompt"), "+10", Loc.T("Vm.Grow.Next"));
        if (input == null) return;

        var txt = input.Trim().Replace(',', '.').TrimEnd('G', 'g', 'B', 'b', ' ');
        bool relative = txt.StartsWith('+');
        if (!double.TryParse(txt.TrimStart('+'), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var gb) || gb <= 0 || gb > 65536)
        {
            Dialog.Error(title, Loc.T("Vm.Grow.ParseError", input));
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
            Dialog.Info(title, Loc.T("Vm.Grow.Nothing"));
            return;
        }

        var lines = string.Join("\n", plan.Take(10).Select(kv =>
            $"  • {kv.Key.DisplayUser}: {kv.Key.MaxSizeText} → {Format.Bytes(kv.Value)}"));
        if (plan.Count > 10) lines += Loc.T("Vm.More", plan.Count - 10);
        var skipped = targets.Count - plan.Count;
        var storage = string.Join("\n", Paths.Where(e => plan.Keys.Any(p => p.RootPath.Equals(e.Path, StringComparison.OrdinalIgnoreCase)))
                                             .Select(e => $"  {e.Path}: {e.SpaceText}"));
        var msg = Loc.T("Vm.Grow.Confirm", plan.Count, lines,
                        skipped > 0 ? Loc.T("Vm.Grow.Skipped", skipped) : "",
                        storage.Length > 0 ? Loc.T("Vm.Grow.Storage", storage) : "");
        if (!Dialog.Confirm(title, msg, Loc.T("Vm.Grow.Verb"), kind: DialogKind.Warning)) return;

        await RunOnTargets(title, (p, r, ct) => ProfileOperations.Grow(p, plan[p], r, ct),
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
        if (targets.Count > 10) who += Loc.T("Vm.More", targets.Count - 10);
        var text = Dialog.Prompt(Loc.T("Vm.Message.Title"),
            Loc.T("Vm.Message.Prompt", who),
            Loc.T("Vm.Message.Default"),
            Loc.T("Vm.Message.Send"), multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;

        IsWorking = true;
        int sent = 0, failed = 0, missing = 0;
        var caption = Loc.T("Vm.Message.Caption");
        try
        {
            foreach (var p in targets)
            {
                p.Busy = Loc.T("Vm.Message.Busy");
                try
                {
                    var sessions = await Task.Run(() => FindSessions(p));
                    if (sessions.Count == 0) { missing++; p.LastResult = Loc.T("Vm.Message.NoSession"); continue; }
                    await Task.Run(() => { foreach (var s in sessions) RdsService.SendMessage(s, caption, text); });
                    sent += sessions.Count;
                    p.LastResult = Loc.T("Vm.Message.Sent", string.Join(", ", sessions));
                    Log.Info(Loc.T("Vm.Message.Log", p.DisplayUser, string.Join(", ", sessions), text.ReplaceLineEndings(" ")));
                }
                catch (Exception ex)
                {
                    failed++;
                    p.SetError(Loc.T("Vm.Message.SendError", ex.Message));
                    Log.Error(Loc.T("Vm.Message.ErrorLog", p.DisplayUser, ex.Message));
                }
                finally { p.Busy = null; }
            }
        }
        finally { IsWorking = false; }
        Status = Loc.T("Vm.Message.Status", sent) + (missing > 0 ? Loc.T("Vm.Message.StatusMissing", missing) : "") +
                 (failed > 0 ? Loc.T("Vm.Message.StatusFailed", failed) : "");
    }

    private async Task LogoffAsync()
    {
        var targets = SessionTargets();
        if (targets.Count == 0) return;

        IsWorking = true;
        var found = new List<(ProfileInfo p, List<RdsSession> sessions)>();
        try
        {
            Status = Loc.T("Vm.Logoff.Searching");
            foreach (var p in targets)
            {
                try { found.Add((p, await Task.Run(() => FindSessions(p)))); }
                catch (Exception ex)
                {
                    p.SetError(Loc.T("Vm.Logoff.SessionsError", ex.Message));
                    Log.Error(Loc.T("Vm.Logoff.SessionsErrorLog", p.DisplayUser, p.StateHost, ex.Message));
                }
            }
        }
        finally { IsWorking = false; SetIdleStatus(); }

        var title = Loc.T("Vm.Logoff.Title");
        var withSessions = found.Where(f => f.sessions.Count > 0).ToList();
        var notFound = targets.Where(t => !withSessions.Any(f => f.p == t)).ToList();
        if (withSessions.Count == 0)
        {
            Dialog.Error(title, Loc.T("Vm.Logoff.NoneFound"));
            return;
        }

        var list = string.Join("\n", withSessions.SelectMany(f => f.sessions).Take(12).Select(s => "  • " + s));
        var msg = Loc.T("Vm.Logoff.Confirm", list,
                        notFound.Count > 0 ? Loc.T("Vm.Logoff.NotFound", notFound.Count) : "");
        if (!Dialog.Confirm(title, msg, Loc.T("Vm.Logoff.Verb"), kind: DialogKind.Danger)) return;

        IsWorking = true;
        int ok = 0, failed = 0;
        try
        {
            foreach (var (p, sessions) in withSessions)
            {
                p.Busy = Loc.T("Vm.Logoff.Busy");
                try
                {
                    foreach (var s in sessions)
                    {
                        Status = Loc.T("Vm.Logoff.Progress", s);
                        Log.Warn(Loc.T("Vm.Logoff.Log", s, p.FilePath));
                        await Task.Run(() => RdsService.Logoff(s));
                        ok++;
                    }
                    p.LastResult = Loc.T("Vm.Logoff.Done", string.Join(", ", sessions.Select(s => $"{s.Host} #{s.Id}")));
                }
                catch (Exception ex)
                {
                    failed++;
                    p.SetError(Loc.T("Vm.Logoff.Error", ex.Message));
                    Log.Error(Loc.T("Vm.Logoff.ErrorLog", p.DisplayUser, ex.Message));
                }
                finally { p.Busy = null; }
            }
        }
        finally { IsWorking = false; }

        // the profile is detached during logoff; give FSLogix a moment, then re-read the state
        Status = Loc.T("Vm.Logoff.Waiting");
        await Task.Delay(TimeSpan.FromSeconds(5));
        await RefreshItemsAsync(withSessions.Select(f => f.p).ToList(), Loc.T("Vm.Refresh.What.AfterLogoff"));
        Status = Loc.T("Vm.Logoff.Status", ok) + (failed > 0 ? Loc.T("Vm.Logoff.StatusFailed", failed) : "") +
                 Loc.T("Vm.Logoff.StatusMounted", withSessions.Count(f => f.p.IsMounted));
    }
}
