using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using Microsoft.Win32;

namespace FSLTool.Localization;

/// <summary>A language the UI can be shown in (one per set of embedded Localization/*.{code}.json tables).</summary>
public sealed record LangInfo(string Code, string NativeName);

/// <summary>
/// UI strings. Tables are embedded JSON files Localization/&lt;area&gt;.&lt;lang&gt;.json (flat "key": "text").
/// Lookup: current language → English → the key itself. The choice is per Windows user
/// (HKCU\Software\FSLTool\Language); empty = follow the Windows display language, English if it is not translated.
/// </summary>
public static class Loc
{
    public const string Fallback = "en";
    private const string ResPrefix = "FSLTool.L.";
    private const string RegKey = @"Software\FSLTool";

    /// <summary>Windows display language at start-up (CurrentUICulture is changed by <see cref="Apply"/>).</summary>
    private static readonly CultureInfo _system = CultureInfo.CurrentUICulture;
    private static readonly Dictionary<string, Dictionary<string, string>> _tables = Load();
    private static Dictionary<string, string> _cur = Table(Fallback);
    private static readonly Dictionary<string, string> _en = Table(Fallback);

    /// <summary>Current language code (uk, en…).</summary>
    public static string Lang { get; private set; } = Fallback;

    /// <summary>Saved choice: null = follow the system.</summary>
    public static string? Preference { get; private set; }

    /// <summary>Raised on the thread that called <see cref="SetLanguage"/> after the language changed.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<LangInfo> Available { get; } = _tables.Keys
        .OrderBy(c => c == "uk" ? 0 : c == "en" ? 1 : 2).ThenBy(c => c)
        .Select(c => new LangInfo(c, _tables[c].TryGetValue("Lang.Native", out var n) ? n : c))
        .ToList();

    /// <summary>Picks the language: <paramref name="forced"/> (headless --lang) → saved choice → system → English.</summary>
    public static void Init(string? forced = null)
    {
        Preference = ReadPreference();
        Apply(Resolve(forced ?? Preference));
    }

    /// <summary>Language for the system UI culture, or English when the app has no such translation.</summary>
    public static string SystemLanguage
    {
        get
        {
            for (var c = _system.Name.Length > 0 ? _system : CultureInfo.InstalledUICulture; c.Name.Length > 0; c = c.Parent)
            {
                if (_tables.ContainsKey(c.Name)) return c.Name;
                if (_tables.ContainsKey(c.TwoLetterISOLanguageName)) return c.TwoLetterISOLanguageName;
            }
            return Fallback;
        }
    }

    public static bool IsSupported(string? code) => code != null && _tables.ContainsKey(code.ToLowerInvariant());

    /// <summary>Switches the language (null = system) and remembers the choice for this Windows user.</summary>
    public static void SetLanguage(string? code)
    {
        code = string.IsNullOrWhiteSpace(code) ? null : code.ToLowerInvariant();
        Preference = code;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RegKey);
            if (code == null) k.DeleteValue("Language", false);
            else k.SetValue("Language", code);
        }
        catch { /* the choice still applies to this session */ }
        var lang = Resolve(code);
        if (lang == Lang) { Changed?.Invoke(); return; }
        Apply(lang);
        Changed?.Invoke();
    }

    /// <summary>Text for <paramref name="key"/> in the current language.</summary>
    public static string T(string key) =>
        _cur.TryGetValue(key, out var s) ? s : _en.TryGetValue(key, out s) ? s : key;

    /// <summary>Formatted text: string.Format(T(key), args) with the current culture.</summary>
    public static string T(string key, params object?[] args)
    {
        var fmt = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch (FormatException) { return fmt; }
    }

    /// <summary>Whether the key exists in the current language or in English.</summary>
    public static bool Has(string key) => _cur.ContainsKey(key) || _en.ContainsKey(key);

    /// <summary>Embedded help: Docs/Help.md is Ukrainian, Docs/Help.&lt;lang&gt;.md the other languages.</summary>
    public static string? HelpResource(string lang) => lang == "uk" ? "FSLTool.Docs.Help.md" : $"FSLTool.Docs.Help.{lang}.md";

    private static string Resolve(string? code) => IsSupported(code) ? code!.ToLowerInvariant() : SystemLanguage;

    private static void Apply(string lang)
    {
        Lang = lang;
        _cur = Table(lang);
        try
        {
            var ci = CultureInfo.GetCultureInfo(lang);
            CultureInfo.DefaultThreadCurrentUICulture = ci;
            CultureInfo.CurrentUICulture = ci;
        }
        catch (CultureNotFoundException) { }
        LocSource.Instance.Raise();
    }

    private static string? ReadPreference()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegKey);
            return k?.GetValue("Language") is string s && IsSupported(s) ? s.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    private static Dictionary<string, string> Table(string lang) =>
        _tables.TryGetValue(lang, out var t) ? t : new(StringComparer.Ordinal);

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            // FSLTool.L.<area>.<lang>.json
            if (!name.StartsWith(ResPrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            var parts = name[ResPrefix.Length..^".json".Length].Split('.');
            if (parts.Length < 2) continue;
            var lang = parts[^1].ToLowerInvariant();
            if (!all.TryGetValue(lang, out var table)) all[lang] = table = new(StringComparer.Ordinal);
            using var s = asm.GetManifestResourceStream(name)!;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(s,
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (d == null) continue;
            foreach (var (k, v) in d) table[k] = v;
        }
        return all;
    }
}

/// <summary>Binding source for XAML: <c>Lang</c> changes whenever the language does, so every {l:Tr} re-evaluates.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Lang => Loc.Lang;
    internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
}

/// <summary>
/// XAML: <c>Text="{l:Tr Main.Scan}"</c>, with bound arguments for {0}, {1}…:
/// <c>Text="{l:Tr Main.RefreshVisible, Arg={Binding VisibleCount}}"</c>. Updates live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";
    public BindingBase? Arg { get; set; }
    public BindingBase? Arg2 { get; set; }
    public BindingBase? Arg3 { get; set; }

    public override object ProvideValue(IServiceProvider sp)
    {
        // a plain CLR string property cannot hold a binding — give it the current text
        if (sp.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetProperty: PropertyInfo { PropertyType: var t } }
            && t == typeof(string))
            return Loc.T(Key);

        var mb = new MultiBinding { Converter = TrConverter.Instance, ConverterParameter = Key, Mode = BindingMode.OneWay };
        mb.Bindings.Add(new Binding(nameof(LocSource.Lang)) { Source = LocSource.Instance, Mode = BindingMode.OneWay });
        if (Arg != null) mb.Bindings.Add(Arg);
        if (Arg2 != null) mb.Bindings.Add(Arg2);
        if (Arg3 != null) mb.Bindings.Add(Arg3);
        return mb.ProvideValue(sp);
    }
}

internal sealed class TrConverter : IMultiValueConverter
{
    public static readonly TrConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var key = parameter as string ?? "";
        if (values.Length <= 1) return Loc.T(key);
        var args = values.Skip(1).Select(v => v == DependencyProperty.UnsetValue || v == BindingOperations.DisconnectedSource ? null : v).ToArray();
        // like Binding.StringFormat: no value → no text (not the fixed words around an empty gap)
        return args.Any(a => a == null) ? "" : Loc.T(key, args);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
