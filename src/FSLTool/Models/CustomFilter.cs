using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FSLTool.ViewModels;

namespace FSLTool.Models;

// ═══════════════════════════════════════════════════════════════════════════════
//  Custom filter: a tree of conditions over any property of a container,
//  combined in groups with І (all) / АБО (any) and optional НЕ (negation).
//  Used by the "Фільтр" button next to the search box and by the headless --where option.
// ═══════════════════════════════════════════════════════════════════════════════

public enum FieldKind { Text, Number, Size, Percent, Date, Bool, Choice }

[JsonConverter(typeof(JsonStringEnumConverter<FilterOp>))]
public enum FilterOp
{
    // text
    Contains, NotContains, Equals, NotEquals, StartsWith, EndsWith, Like, NotLike, Regex, NotRegex,
    // numbers / sizes / percent
    Gt, Ge, Lt, Le, Eq, Ne, Between,
    // dates
    OlderThanDays, NewerThanDays, Before, After, DateBetween,
    // bool
    IsTrue, IsFalse,
    // choice
    Is, IsNot,
    // any kind
    Empty, NotEmpty,
}

[JsonConverter(typeof(JsonStringEnumConverter<GroupMode>))]
public enum GroupMode { All, Any }

/// <summary>
/// A value with a translated label (operator, group mode, choice). <see cref="Label"/> is looked up on every read and
/// raises PropertyChanged when the language changes — create only long-lived (static) instances: each one stays
/// subscribed to <see cref="LocSource"/>.
/// </summary>
public sealed class Option<T> : INotifyPropertyChanged
{
    public Option(T value, string key)
    {
        Value = value;
        Key = key;
        LocSource.Instance.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    public T Value { get; }
    /// <summary>Localization key of the label.</summary>
    public string Key { get; }
    public string Label => Loc.T(Key);
    public event PropertyChangedEventHandler? PropertyChanged;
    // shown in the closed combo box
    public override string ToString() => Label;
}

/// <summary>A property of <see cref="ProfileInfo"/> that a condition can test.</summary>
public sealed class FilterField : INotifyPropertyChanged
{
    // the fields are static (FilterFields.All): stay subscribed for the lifetime of the app
    public FilterField() =>
        LocSource.Instance.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Invariant id (saved in filters.json, used in localization keys Filter.Field.* / Filter.Hint.*).</summary>
    public required string Key { get; init; }
    /// <summary>Invariant id of the topic (key Filter.FieldGroup.*).</summary>
    public required string GroupKey { get; init; }
    public required FieldKind Kind { get; init; }
    /// <summary>Value of the property; for <see cref="FieldKind.Choice"/> a language-independent code from <see cref="Choices"/>.</summary>
    public required Func<ProfileInfo, object?> Get { get; init; }
    /// <summary>Allowed codes (Value) with translated labels, for <see cref="FieldKind.Choice"/>.</summary>
    public IReadOnlyList<Option<string>>? Choices { get; init; }
    /// <summary>Old saved values (Ukrainian display texts before codes were introduced) → code.</summary>
    public IReadOnlyDictionary<string, string>? LegacyValues { get; init; }
    public bool HasHint { get; init; }

    public string Label => Loc.T("Filter.Field." + FilterFields.Cap(Key));
    public string Group => Loc.T("Filter.FieldGroup." + GroupKey);
    public string? Hint => HasHint ? Loc.T("Filter.Hint." + FilterFields.Cap(Key)) : null;

    /// <summary>Choice code for <paramref name="v"/>: the code itself (any case) or a legacy saved text; otherwise unchanged.</summary>
    public string NormalizeChoice(string v)
    {
        if (Choices == null) return v;
        var hit = Choices.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.Value;
        return LegacyValues != null && LegacyValues.TryGetValue(v.Trim(), out var code) ? code : v;
    }

    /// <summary>Translated label of a choice code (the code itself when unknown).</summary>
    public string ChoiceLabel(string v) =>
        Choices?.FirstOrDefault(c => string.Equals(c.Value, v, StringComparison.OrdinalIgnoreCase))?.Label ?? v;

    public override string ToString() => Label;
}

public static class FilterFields
{
    private static string? Txt(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>"folderUser" → "FolderUser" (localization key segment).</summary>
    internal static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Choice options: code → key Filter.Choice.{field}.{Code}.</summary>
    private static Option<string>[] Ch(string field, params string[] codes) =>
        codes.Select(c => new Option<string>(c, $"Filter.Choice.{field}.{Cap(c)}")).ToArray();

    private static Dictionary<string, string> Legacy(params (string Text, string Code)[] map) =>
        map.ToDictionary(m => m.Text, m => m.Code, StringComparer.OrdinalIgnoreCase);

    private static string SeverityCode(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "warning",
        _ => "ok",
    };

    private static string StateCode(MountState s) => s switch
    {
        MountState.Free => "free",
        MountState.InUseSmb => "session",
        MountState.AttachedLocal => "local",
        MountState.Locked => "locked",
        _ => "none",
    };

    public static readonly IReadOnlyList<FilterField> All = new List<FilterField>
    {
        // ── account ──
        new() { Key = "user", GroupKey = "Account", Kind = FieldKind.Text, Get = p => p.DisplayUser,
                HasHint = true },
        new() { Key = "account", GroupKey = "Account", Kind = FieldKind.Text, Get = p => Txt(p.AccountName) },
        new() { Key = "folderUser", GroupKey = "Account", Kind = FieldKind.Text, Get = p => Txt(p.UserName) },
        new() { Key = "sid", GroupKey = "Account", Kind = FieldKind.Text, Get = p => p.Sid },
        new() { Key = "orphan", GroupKey = "Account", Kind = FieldKind.Bool, Get = p => p.IsOrphan },
        new() { Key = "disabled", GroupKey = "Account", Kind = FieldKind.Bool, Get = p => p.IsDisabled },
        new() { Key = "adState", GroupKey = "Account", Kind = FieldKind.Text, Get = p => p.AdText == "—" ? null : p.AdText },
        new() { Key = "lastLogon", GroupKey = "Account", Kind = FieldKind.Date, Get = p => p.LastLogon },

        // ── file ──
        new() { Key = "path", GroupKey = "File", Kind = FieldKind.Text, Get = p => p.FilePath },
        new() { Key = "folder", GroupKey = "File", Kind = FieldKind.Text, Get = p => p.FolderName },
        new() { Key = "file", GroupKey = "File", Kind = FieldKind.Text, Get = p => p.FileName },
        new() { Key = "root", GroupKey = "File", Kind = FieldKind.Text, Get = p => p.RootPath },
        new() { Key = "type", GroupKey = "File", Kind = FieldKind.Choice, Get = p => p.ContainerType.ToString(),
                Choices = Ch("Type", "Profile", "ODFC", "Unknown") },
        new() { Key = "format", GroupKey = "File", Kind = FieldKind.Choice, Get = p => p.DiskFormat, Choices = Ch("Format", "VHDX", "VHD") },
        new() { Key = "size", GroupKey = "File", Kind = FieldKind.Size, Get = p => p.FileSize },
        new() { Key = "maxSize", GroupKey = "File", Kind = FieldKind.Size, Get = p => p.VirtualSize },
        new() { Key = "lastWrite", GroupKey = "File", Kind = FieldKind.Date, Get = p => p.LastWrite },
        new() { Key = "stale", GroupKey = "File", Kind = FieldKind.Bool, Get = p => p.IsStale },
        new() { Key = "diff", GroupKey = "File", Kind = FieldKind.Bool, Get = p => p.HasDiffDisk },
        new() { Key = "deletable", GroupKey = "File", Kind = FieldKind.Bool, Get = p => p.IsDeletable,
                HasHint = true },

        // ── usage ──
        new() { Key = "usage", GroupKey = "Usage", Kind = FieldKind.Percent, Get = p => p.UsagePercent },
        new() { Key = "severity", GroupKey = "Usage", Kind = FieldKind.Choice, Get = p => SeverityCode(p.Severity),
                Choices = Ch("Severity", "ok", "warning", "critical"),
                LegacyValues = Legacy(("Норма", "ok"), ("Близький до межі", "warning"), ("Переповнений", "critical")) },
        new() { Key = "exact", GroupKey = "Usage", Kind = FieldKind.Bool, Get = p => p.UsageIsExact },
        new() { Key = "volUsed", GroupKey = "Usage", Kind = FieldKind.Size, Get = p => p.UsageIsExact ? p.VolumeUsed : null },
        new() { Key = "volTotal", GroupKey = "Usage", Kind = FieldKind.Size, Get = p => p.UsageIsExact ? p.VolumeTotal : null },
        new() { Key = "volFree", GroupKey = "Usage", Kind = FieldKind.Size,
                Get = p => p.UsageIsExact ? p.VolumeTotal!.Value - p.VolumeUsed!.Value : null },
        new() { Key = "reclaimable", GroupKey = "Usage", Kind = FieldKind.Size, Get = p => p.Reclaimable },
        new() { Key = "worth", GroupKey = "Usage", Kind = FieldKind.Bool, Get = p => p.WorthCompacting },
        new() { Key = "estimate", GroupKey = "Usage", Kind = FieldKind.Size,
                Get = p => p.Estimates?.Sum(e => e.Bytes) },

        // ── growth ──
        new() { Key = "growth", GroupKey = "Growth", Kind = FieldKind.Size, Get = p => p.Growth },
        new() { Key = "dailyGrowth", GroupKey = "Growth", Kind = FieldKind.Size, Get = p => p.DailyGrowth is double d ? (long)d : null },
        new() { Key = "daysToLimit", GroupKey = "Growth", Kind = FieldKind.Number, Get = p => p.DaysToLimit },
        new() { Key = "etaSoon", GroupKey = "Growth", Kind = FieldKind.Bool, Get = p => p.EtaSoon },

        // ── state ──
        new() { Key = "state", GroupKey = "State", Kind = FieldKind.Choice, Get = p => StateCode(p.State),
                Choices = Ch("State", "free", "session", "local", "locked", "none"),
                LegacyValues = Legacy(("Вільний", "free"), ("У сесії", "session"), ("Локально", "local"), ("Заблоковано", "locked"), ("—", "none")) },
        new() { Key = "mounted", GroupKey = "State", Kind = FieldKind.Bool, Get = p => p.IsMounted },
        new() { Key = "hosts", GroupKey = "State", Kind = FieldKind.Text, Get = p => Txt(p.StateHost) },
        new() { Key = "mountedBy", GroupKey = "State", Kind = FieldKind.Text, Get = p => p.Handles.Count == 0 ? null : p.MountedBy },
        new() { Key = "handles", GroupKey = "State", Kind = FieldKind.Number, Get = p => p.Handles.Count },
        new() { Key = "fs", GroupKey = "State", Kind = FieldKind.Choice,
                Get = p => p.FsDirty switch { true => "dirty", false => "clean", _ => "unknown" },
                Choices = Ch("Fs", "dirty", "clean", "unknown"),
                LegacyValues = Legacy(("Потребує перевірки", "dirty"), ("Без позначки помилок", "clean"), ("Невідомо", "unknown")) },
        new() { Key = "flags", GroupKey = "State", Kind = FieldKind.Text, Get = p => Txt(p.FlagsText),
                HasHint = true },
        new() { Key = "checked", GroupKey = "State", Kind = FieldKind.Bool, Get = p => p.IsChecked },
        new() { Key = "busy", GroupKey = "State", Kind = FieldKind.Bool, Get = p => p.IsBusy },

        // ── analysis ──
        new() { Key = "analyzedAt", GroupKey = "Analysis", Kind = FieldKind.Date, Get = p => p.AnalyzedAt },
        new() { Key = "refreshedAt", GroupKey = "Analysis", Kind = FieldKind.Date, Get = p => p.RefreshedAt },
        new() { Key = "topFolders", GroupKey = "Analysis", Kind = FieldKind.Text, Get = p => FolderPaths(p.FolderTree),
                HasHint = true },
        new() { Key = "topFiles", GroupKey = "Analysis", Kind = FieldKind.Text,
                Get = p => p.TopFiles is { Count: > 0 } f ? string.Join("\n", f.Select(x => x.Path)) : null,
                HasHint = true },
        new() { Key = "largestFile", GroupKey = "Analysis", Kind = FieldKind.Size, Get = p => p.TopFiles is { Count: > 0 } f ? f.Max(x => x.Size) : null },
        new() { Key = "lastResult", GroupKey = "Analysis", Kind = FieldKind.Text, Get = p => Txt(p.LastResult) },
    };

    private static readonly Dictionary<string, FilterField> ByKey = All.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    public static FilterField? Find(string? key) => key != null && ByKey.TryGetValue(key, out var f) ? f : null;

    private static string? FolderPaths(FolderNode? root)
    {
        if (root == null) return null;
        var sb = new System.Text.StringBuilder();
        void Walk(FolderNode n) { foreach (var c in n.Children) { sb.Append(c.Path).Append('\n'); Walk(c); } }
        Walk(root);
        return sb.Length == 0 ? null : sb.ToString(0, sb.Length - 1);
    }

    /// <summary>Operators offered for a kind of field (first one is the default).</summary>
    public static IReadOnlyList<Option<FilterOp>> OperatorsFor(FieldKind kind) => kind switch
    {
        FieldKind.Text => TextOps,
        FieldKind.Number or FieldKind.Size or FieldKind.Percent => NumberOps,
        FieldKind.Date => DateOps,
        FieldKind.Bool => BoolOps,
        _ => ChoiceOps,
    };

    private static readonly Option<FilterOp>[] TextOps =
    {
        Op(FilterOp.Contains), Op(FilterOp.NotContains), Op(FilterOp.Equals), Op(FilterOp.NotEquals),
        Op(FilterOp.StartsWith), Op(FilterOp.EndsWith), Op(FilterOp.Like), Op(FilterOp.NotLike),
        Op(FilterOp.Regex), Op(FilterOp.NotRegex), Op(FilterOp.Empty), Op(FilterOp.NotEmpty),
    };
    private static readonly Option<FilterOp>[] NumberOps =
    {
        Op(FilterOp.Ge), Op(FilterOp.Gt), Op(FilterOp.Le), Op(FilterOp.Lt), Op(FilterOp.Eq), Op(FilterOp.Ne), Op(FilterOp.Between),
        Op(FilterOp.Empty, "Unknown"), Op(FilterOp.NotEmpty, "Known"),
    };
    private static readonly Option<FilterOp>[] DateOps =
    {
        Op(FilterOp.OlderThanDays), Op(FilterOp.NewerThanDays), Op(FilterOp.Before), Op(FilterOp.After), Op(FilterOp.DateBetween),
        Op(FilterOp.Empty, "Unknown"), Op(FilterOp.NotEmpty, "Known"),
    };
    private static readonly Option<FilterOp>[] BoolOps = { Op(FilterOp.IsTrue), Op(FilterOp.IsFalse) };
    private static readonly Option<FilterOp>[] ChoiceOps = { Op(FilterOp.Is, "Equals"), Op(FilterOp.IsNot, "NotEquals") };

    /// <summary>Operator option with the label key Filter.Op.{name} (default: the operator's own name).</summary>
    private static Option<FilterOp> Op(FilterOp op, string? name = null) => new(op, "Filter.Op." + (name ?? op.ToString()));

    public static string OpLabel(FilterOp op) =>
        TextOps.Concat(NumberOps).Concat(DateOps).Concat(BoolOps).Concat(ChoiceOps).FirstOrDefault(o => o.Value == op)?.Label ?? op.ToString();

    // ───── value parsing ─────

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryNumber(string? s, out double v)
    {
        s = (s ?? "").Trim().Replace(" ", "").Replace(' '.ToString(), "").TrimEnd('%').Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, Inv, out v);
    }

    /// <summary>"10" (GB by default), "500 MB", "1.5 TB", "2048KB", "123 B" → bytes.</summary>
    public static bool TrySize(string? s, out double bytes)
    {
        bytes = 0;
        var m = System.Text.RegularExpressions.Regex.Match((s ?? "").Trim(),
            @"^([0-9]+(?:[.,][0-9]+)?)\s*(b|kb|k|mb|m|gb|g|tb|t|б|кб|мб|гб|тб)?$", RegexOptions.IgnoreCase);
        if (!m.Success || !TryNumber(m.Groups[1].Value, out var n)) return false;
        int pow = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "b" or "б" => 0,
            "kb" or "k" or "кб" => 1,
            "mb" or "m" or "мб" => 2,
            "tb" or "t" or "тб" => 4,
            _ => 3,   // GB, also when no unit is given
        };
        bytes = n * Math.Pow(1024, pow);
        return true;
    }

    private static readonly string[] DateFormats = { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd", "dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm" };
    public static bool TryDate(string? s, out DateTime d) =>
        DateTime.TryParseExact((s ?? "").Trim(), DateFormats, Inv, DateTimeStyles.AllowWhiteSpaces, out d);
}

// ───────────────────────────────── tree ─────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "node")]
[JsonDerivedType(typeof(FilterCondition), "condition")]
[JsonDerivedType(typeof(FilterGroup), "group")]
public abstract class FilterNode : ObservableObject
{
    /// <summary>Raised on any change of this node or its children (the view re-filters).</summary>
    public event Action? Changed;
    protected void RaiseChanged() => Changed?.Invoke();

    [JsonIgnore] public FilterGroup? Parent { get; set; }
    [JsonIgnore] public bool IsRoot => Parent == null;

    private bool _isEnabled = true;
    /// <summary>A switched-off node is ignored (as if it were not there).</summary>
    public bool IsEnabled { get => _isEnabled; set { if (Set(ref _isEnabled, value)) RaiseChanged(); } }

    public abstract bool Evaluate(ProfileInfo p);
    /// <summary>Has at least one enabled, valid condition (otherwise the node does not restrict anything).</summary>
    [JsonIgnore] public abstract bool IsActive { get; }
    /// <summary>Human-readable expression for the filter bar.</summary>
    public abstract string Describe();

    /// <summary>Re-reads translated texts after the UI language changed (recursively for groups).</summary>
    public virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);
}

public sealed class FilterCondition : FilterNode
{
    private string _field = "usage";
    [JsonPropertyOrder(-3)]
    public string Field
    {
        get => _field;
        set
        {
            var old = FilterFields.Find(_field);
            if (!Set(ref _field, value)) return;
            var f = FieldDef;
            // keep the operator if it still fits the new field, otherwise take the default
            if (f != null && (old?.Kind != f.Kind || !Operators.Any(o => o.Value == Op))) Op = Operators[0].Value;
            if (f is { Kind: FieldKind.Choice, Choices: { } ch } && !ch.Any(c => c.Value == Value)) Value = ch[0].Value;
            else if (old?.Kind != f?.Kind) { Value = ""; Value2 = ""; }
            OnPropertyChanged(nameof(Operators));
            OnPropertyChanged(nameof(FieldDef));
            OnPropertyChanged(nameof(Choices));
            OnPropertyChanged(nameof(FieldHint));
            // the editor's combo boxes got new item lists: re-push the current operator / value into them
            OnPropertyChanged(nameof(Op));
            OnPropertyChanged(nameof(Value));
            UpdateEditors();
            Validate();
            RaiseChanged();
        }
    }

    private FilterOp _op = FilterOp.Ge;
    [JsonPropertyOrder(-2)]
    public FilterOp Op { get => _op; set { if (Set(ref _op, value)) { _rx = null; UpdateEditors(); Validate(); RaiseChanged(); } } }

    private string _value = "80";
    // null comes only from a combo box whose item list is being replaced — ignore it
    [JsonPropertyOrder(-1)]
    public string Value { get => _value; set { if (value != null && Set(ref _value, value)) { _rx = null; Validate(); RaiseChanged(); } } }

    private string _value2 = "";
    /// <summary>Upper bound for "від … до" / "між датами".</summary>
    public string Value2 { get => _value2; set { if (Set(ref _value2, value ?? "")) { Validate(); RaiseChanged(); } } }

    // ───── editor state (not saved) ─────

    [JsonIgnore] public FilterField? FieldDef => FilterFields.Find(Field);
    [JsonIgnore] public IReadOnlyList<Option<FilterOp>> Operators => FilterFields.OperatorsFor(FieldDef?.Kind ?? FieldKind.Text);
    /// <summary>Choice codes (Value) with translated labels (Label) for the value combo box.</summary>
    [JsonIgnore] public IReadOnlyList<Option<string>>? Choices => FieldDef?.Choices;
    [JsonIgnore] public string? FieldHint => FieldDef?.Hint;

    [JsonIgnore] public bool ShowText => !ShowChoice && NeedsValue;
    [JsonIgnore] public bool ShowChoice => FieldDef?.Kind == FieldKind.Choice;
    [JsonIgnore] public bool ShowSecond => Op is FilterOp.Between or FilterOp.DateBetween;
    [JsonIgnore] public string Unit => FieldDef?.Kind switch
    {
        _ when !NeedsValue => "",
        FieldKind.Size => "GB",
        FieldKind.Percent => "%",
        FieldKind.Date when Op is FilterOp.OlderThanDays or FilterOp.NewerThanDays => Loc.T("Filter.Unit.Days"),
        _ => "",
    };
    [JsonIgnore] public string Placeholder => FieldDef?.Kind switch
    {
        FieldKind.Size => Loc.T("Filter.Placeholder.Size"),
        FieldKind.Date when Op is FilterOp.OlderThanDays or FilterOp.NewerThanDays => Loc.T("Filter.Placeholder.Days"),
        FieldKind.Date => Loc.T("Filter.Placeholder.Date"),
        FieldKind.Text when Op is FilterOp.Like or FilterOp.NotLike => Loc.T("Filter.Placeholder.Like"),
        FieldKind.Text when Op is FilterOp.Regex or FilterOp.NotRegex => Loc.T("Filter.Placeholder.Regex"),
        _ => "",
    };

    private bool NeedsValue => Op is not (FilterOp.Empty or FilterOp.NotEmpty or FilterOp.IsTrue or FilterOp.IsFalse);

    private void UpdateEditors()
    {
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ShowChoice));
        OnPropertyChanged(nameof(ShowSecond));
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(Placeholder));
    }

    private string? _error;
    /// <summary>Why the value cannot be used; such a condition is ignored until it is fixed.</summary>
    [JsonIgnore] public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Called after loading from JSON (setters ran in arbitrary order).</summary>
    public void Init()
    {
        // filters saved before choice codes stored the Ukrainian display text ("Переповнений", "У сесії"…)
        if (FieldDef is { Kind: FieldKind.Choice } cf)
        {
            var code = cf.NormalizeChoice(_value);
            if (code != _value) { _value = code; _rx = null; OnPropertyChanged(nameof(Value)); }
        }
        UpdateEditors(); OnPropertyChanged(nameof(Operators)); OnPropertyChanged(nameof(Choices)); Validate();
    }

    public override void OnLanguageChanged()
    {
        Validate();   // the error text is stored translated
        base.OnLanguageChanged();
    }

    private void Validate()
    {
        var f = FieldDef;
        if (f == null) { Error = Loc.T("Filter.Error.UnknownField", Field); return; }
        if (!Operators.Any(o => o.Value == Op)) { Error = Loc.T("Filter.Error.BadOperator"); return; }
        Error = NeedsValue ? CheckValue(f.Kind, Value) ?? (ShowSecond ? CheckValue(f.Kind, Value2) : null) : null;
    }

    private string? CheckValue(FieldKind kind, string v)
    {
        switch (kind)
        {
            case FieldKind.Size when !FilterFields.TrySize(v, out _): return Loc.T("Filter.Error.Size");
            case FieldKind.Number or FieldKind.Percent when !FilterFields.TryNumber(v, out _): return Loc.T("Filter.Error.Number");
            case FieldKind.Date when Op is FilterOp.OlderThanDays or FilterOp.NewerThanDays:
                return FilterFields.TryNumber(v, out var d) && d >= 0 ? null : Loc.T("Filter.Error.Days");
            case FieldKind.Date when !FilterFields.TryDate(v, out _): return Loc.T("Filter.Error.Date");
            case FieldKind.Text when Op is FilterOp.Regex or FilterOp.NotRegex:
                try { _ = new Regex(v); return null; } catch (ArgumentException ex) { return Loc.T("Filter.Error.Regex", ex.Message); }
            case FieldKind.Text when v.Length == 0 && Op is not (FilterOp.Equals or FilterOp.NotEquals):
                return Loc.T("Filter.Error.Text");
            default: return null;
        }
    }

    [JsonIgnore] public override bool IsActive => IsEnabled && Error == null;

    private Regex? _rx;
    private Regex Rx => _rx ??= Op is FilterOp.Like or FilterOp.NotLike
        ? new Regex("^" + System.Text.RegularExpressions.Regex.Escape(Value).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(200))
        : new Regex(Value, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(200));

    public override bool Evaluate(ProfileInfo p)
    {
        var f = FieldDef!;
        object? raw = f.Get(p);
        if (Op == FilterOp.Empty) return raw is null || raw is string { Length: 0 };
        if (Op == FilterOp.NotEmpty) return !(raw is null || raw is string { Length: 0 });

        switch (f.Kind)
        {
            case FieldKind.Bool:
                return (raw is true) == (Op == FilterOp.IsTrue);

            case FieldKind.Choice:
                bool eq = string.Equals(raw as string, Value, StringComparison.OrdinalIgnoreCase);
                return Op == FilterOp.Is ? eq : !eq;

            case FieldKind.Text:
                return MatchText(raw as string ?? "");

            case FieldKind.Date:
                if (raw is not DateTime dt) return false;
                switch (Op)
                {
                    case FilterOp.OlderThanDays: FilterFields.TryNumber(Value, out var n1); return dt < DateTime.Now.AddDays(-n1);
                    case FilterOp.NewerThanDays: FilterFields.TryNumber(Value, out var n2); return dt >= DateTime.Now.AddDays(-n2);
                }
                FilterFields.TryDate(Value, out var d1);
                return Op switch
                {
                    FilterOp.Before => dt < d1,
                    FilterOp.After => dt >= d1.AddDays(d1.TimeOfDay == TimeSpan.Zero ? 1 : 0),   // "після 01.03" = from 02.03
                    FilterOp.DateBetween => FilterFields.TryDate(Value2, out var d2) && dt >= d1 && dt < d2.AddDays(d2.TimeOfDay == TimeSpan.Zero ? 1 : 0),
                    _ => false,
                };

            default:   // Number / Size / Percent
                double? x = raw switch { long l => l, int i => i, double d => d, _ => null };
                if (x is not double v) return false;
                double a = Num(Value), b = ShowSecond ? Num(Value2) : 0;
                return Op switch
                {
                    FilterOp.Gt => v > a,
                    FilterOp.Ge => v >= a,
                    FilterOp.Lt => v < a,
                    FilterOp.Le => v <= a,
                    FilterOp.Eq => Math.Abs(v - a) < Tolerance(f.Kind, a),
                    FilterOp.Ne => Math.Abs(v - a) >= Tolerance(f.Kind, a),
                    FilterOp.Between => v >= Math.Min(a, b) && v <= Math.Max(a, b),
                    _ => false,
                };
        }
    }

    /// <summary>"= 10 GB" should match a 10.004 GB file; "= 80 %" a 80.4 % usage.</summary>
    private static double Tolerance(FieldKind k, double a) => k switch
    {
        FieldKind.Size => Math.Max(1, Math.Abs(a) * 0.005),
        FieldKind.Percent => 0.5,
        _ => 1e-9,
    };

    private double Num(string s) => FieldDef!.Kind == FieldKind.Size
        ? (FilterFields.TrySize(s, out var b) ? b : 0)
        : (FilterFields.TryNumber(s, out var n) ? n : 0);

    private bool MatchText(string s)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        try
        {
            return Op switch
            {
                FilterOp.Contains => s.Contains(Value, ci),
                FilterOp.NotContains => !s.Contains(Value, ci),
                // multi-line fields (folders, files): equality / prefix / suffix apply to any of the lines
                FilterOp.Equals => Lines(s).Any(l => l.Equals(Value, ci)),
                FilterOp.NotEquals => !Lines(s).Any(l => l.Equals(Value, ci)),
                FilterOp.StartsWith => Lines(s).Any(l => l.StartsWith(Value, ci)),
                FilterOp.EndsWith => Lines(s).Any(l => l.EndsWith(Value, ci)),
                FilterOp.Like or FilterOp.Regex => Rx.IsMatch(s),
                FilterOp.NotLike or FilterOp.NotRegex => !Rx.IsMatch(s),
                _ => false,
            };
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static string[] Lines(string s) => s.Contains('\n') ? s.Split('\n') : new[] { s };

    public override string Describe()
    {
        var f = FieldDef;
        string name = f?.Label ?? Field;
        string op = FilterFields.OpLabel(Op);
        if (!NeedsValue)
            return f?.Kind == FieldKind.Bool
                ? (Op == FilterOp.IsTrue ? name : Loc.T("Filter.Describe.Not", LowerFirst(name)))
                : Loc.T("Filter.Describe.Unary", name, op);
        string unit = Unit.Length > 0 && f?.Kind != FieldKind.Size ? " " + Unit : "";
        string val = f?.Kind == FieldKind.Size && FilterFields.TryNumber(Value, out _) ? Value + " GB"
                   : f?.Kind == FieldKind.Choice ? f.ChoiceLabel(Value) : Value;
        string val2 = f?.Kind == FieldKind.Size && FilterFields.TryNumber(Value2, out _) ? Value2 + " GB" : Value2;
        return Op switch
        {
            FilterOp.Between or FilterOp.DateBetween => Loc.T("Filter.Describe.Between", name, val, val2, unit),
            FilterOp.OlderThanDays => Loc.T("Filter.Describe.OlderThan", name, Value),
            FilterOp.NewerThanDays => Loc.T("Filter.Describe.NewerThan", name, Value),
            _ when f?.Kind == FieldKind.Text => Loc.T("Filter.Describe.Text", name, op, Value),
            _ => Loc.T("Filter.Describe.Value", name, op, val, unit),
        };
    }

    // "Сирота…" → "сирота…", but keep abbreviations ("SID…", "AD…") as they are
    private static string LowerFirst(string s) =>
        s.Length == 0 || (s.Length > 1 && char.IsUpper(s[1])) ? s : char.ToLower(s[0]) + s[1..];
}

public sealed class FilterGroup : FilterNode
{
    public static IReadOnlyList<Option<GroupMode>> ModeOptions { get; } = new[]
    {
        new Option<GroupMode>(GroupMode.All, "Filter.Mode.All"),
        new Option<GroupMode>(GroupMode.Any, "Filter.Mode.Any"),
    };

    private GroupMode _mode = GroupMode.All;
    public GroupMode Mode { get => _mode; set { if (Set(ref _mode, value)) RaiseChanged(); } }

    private bool _negate;
    /// <summary>НЕ: the group matches when its contents do NOT match.</summary>
    public bool Negate { get => _negate; set { if (Set(ref _negate, value)) RaiseChanged(); } }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<FilterNode> Items { get; } = new();

    public FilterGroup()
    {
        Items.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (FilterNode n in e.OldItems) { n.Changed -= RaiseChanged; n.Parent = null; }
        if (e.NewItems != null)
            foreach (FilterNode n in e.NewItems) { n.Changed += RaiseChanged; n.Parent = this; }
        OnPropertyChanged(nameof(IsEmpty));
        RaiseChanged();
    }

    [JsonIgnore] public bool IsEmpty => Items.Count == 0;

    [JsonIgnore] public override bool IsActive => IsEnabled && Items.Any(i => i.IsActive);

    public override bool Evaluate(ProfileInfo p)
    {
        bool result = true;
        bool any = false;
        foreach (var i in Items)
        {
            if (!i.IsActive) continue;
            bool r = i.Evaluate(p);
            if (Mode == GroupMode.All) { if (!r) { result = false; break; } }
            else { any = true; if (r) { result = true; break; } result = false; }
        }
        if (Mode == GroupMode.Any && !any) result = true;
        return Negate ? !result : result;
    }

    /// <summary>The whole tree as a predicate; an inactive tree matches everything.</summary>
    public bool Matches(ProfileInfo p) => !IsActive || Evaluate(p);

    public override string Describe()
    {
        var parts = Items.Where(i => i.IsActive).Select(i => i is FilterGroup g && g.Items.Count(x => x.IsActive) > 1 && !g.Negate
            ? "(" + g.Describe() + ")" : i.Describe()).ToList();
        string body = string.Join(Loc.T(Mode == GroupMode.All ? "Filter.Describe.And" : "Filter.Describe.Or"), parts);
        return Negate ? Loc.T("Filter.Describe.NotGroup", body) : body;
    }

    public override void OnLanguageChanged()
    {
        foreach (var i in Items) i.OnLanguageChanged();
        base.OnLanguageChanged();
    }

    /// <summary>Number of active conditions in the tree (for the badge on the button).</summary>
    public int ActiveConditionCount() => !IsEnabled ? 0
        : Items.Sum(i => i switch { FilterGroup g => g.ActiveConditionCount(), _ => i.IsActive ? 1 : 0 });

    /// <summary>Switched-on conditions (in switched-on groups) whose value is invalid — the UI ignores them, the headless --where refuses to run.</summary>
    public IEnumerable<FilterCondition> BrokenConditions() => !IsEnabled ? Enumerable.Empty<FilterCondition>()
        : Items.SelectMany(i => i switch
        {
            FilterGroup g => g.BrokenConditions(),
            FilterCondition { IsEnabled: true, Error: not null } c => new[] { c },
            _ => Enumerable.Empty<FilterCondition>(),
        });

    /// <summary>Restores parent links / editor state after deserialization.</summary>
    public void Init()
    {
        foreach (var i in Items)
        {
            i.Parent = this;
            if (i is FilterGroup g) g.Init();
            else ((FilterCondition)i).Init();
        }
    }

    public FilterGroup Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, FSLTool.Services.Storage.Json);
        var g = System.Text.Json.JsonSerializer.Deserialize<FilterGroup>(json, FSLTool.Services.Storage.Json)!;
        g.Init();
        return g;
    }
}

/// <summary>A named filter in %ProgramData%\FSLTool\filters.json (shared by all admins and the headless --where).</summary>
public sealed class SavedFilter
{
    public string Name { get; set; } = "";
    public FilterGroup Filter { get; set; } = new();
    public override string ToString() => Name;
}
