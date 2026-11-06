using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FSLTool.Views;

/// <summary>Theme choice: follow Windows (light / dark app mode), or always light / dark.</summary>
public enum AppTheme { System, Light, Dark }

/// <summary>
/// Light / dark theme. The palette is a merged dictionary of the application (Themes/Palette.Light.xaml or
/// Palette.Dark.xaml) that is swapped at run time; styles and windows use the colours through DynamicResource.
/// The choice is per Windows user (HKCU\Software\FSLTool\Theme = light | dark; no value = follow Windows).
/// </summary>
public static class ThemeManager
{
    private const string RegKey = @"Software\FSLTool";
    private const string PaletteMarker = "Themes/Palette.";

    public static AppTheme Preference { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; }

    /// <summary>Raised on the UI thread after the palette changed.</summary>
    public static event Action? Changed;

    /// <summary>Loads the embedded font and the saved theme. Call once, before the first window.</summary>
    public static void Init(Application app)
    {
        // Manrope (Assets/Fonts, SIL OFL) with Segoe UI as the fallback for glyphs it does not have
        app.Resources["UiFont"] = new FontFamily(new Uri("pack://application:,,,/FSLTool;component/"), "./Assets/Fonts/#Manrope, Segoe UI");
        Preference = ReadPreference();
        Apply(force: true);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || Preference != AppTheme.System) return;
            app.Dispatcher.BeginInvoke(() => Apply(force: false));
        };
    }

    /// <summary>Switches the theme and remembers the choice for this Windows user.</summary>
    public static void Set(AppTheme mode)
    {
        Preference = mode;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RegKey);
            if (mode == AppTheme.System) k.DeleteValue("Theme", false);
            else k.SetValue("Theme", mode == AppTheme.Dark ? "dark" : "light");
        }
        catch { /* the choice still applies to this session */ }
        Apply(force: false);
    }

    /// <summary>Header button: light ⇄ dark (an explicit choice, no longer following Windows).</summary>
    public static void Toggle() => Set(IsDark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Brush of the current palette (code that cannot use DynamicResource).</summary>
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static void Apply(bool force)
    {
        bool dark = Preference == AppTheme.Dark || (Preference == AppTheme.System && SystemIsDark());
        if (!force && dark == IsDark) return;
        IsDark = dark;

        var app = Application.Current;
        if (app == null) return;
        var merged = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/FSLTool;component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml")
        };
        int i = merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains(PaletteMarker, StringComparison.OrdinalIgnoreCase) == true);
        if (i >= 0) merged[i] = palette;
        else merged.Insert(0, palette);

        foreach (Window w in app.Windows) WindowChrome.Apply(w);
        Changed?.Invoke();
    }

    /// <summary>Windows "app mode" (Settings › Personalization › Colors).</summary>
    private static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static AppTheme ReadPreference()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegKey);
            return (k?.GetValue("Theme") as string)?.ToLowerInvariant() switch
            {
                "dark" => AppTheme.Dark,
                "light" => AppTheme.Light,
                _ => AppTheme.System,
            };
        }
        catch { return AppTheme.System; }
    }
}
