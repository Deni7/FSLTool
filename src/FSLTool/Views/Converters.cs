using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FSLTool.Models;

namespace FSLTool.Views;

/// <summary>
/// Severity / MountState → brush of the current palette. Parameter: none = solid (bars, dots),
/// "soft" = chip background, "text" = text on that background.
/// Bindings re-read it when the theme changes (rows and paths raise their properties again, see MainViewModel).
/// </summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string family = value switch
        {
            Severity.Critical => "Crit",
            Severity.Warning => "Warn",
            Severity.Ok => "Usage",
            MountState.InUseSmb or MountState.AttachedLocal => "Mount",
            MountState.Locked => "Warn",
            MountState.Free => "Ok",
            _ => "Neutral",
        };
        string key = (p as string, family) switch
        {
            ("soft", "Usage") => "AccentSoft",
            ("text", "Usage") => "AccentText",
            (_, "Usage") => "UsageOk",
            ("soft", _) => family + "Soft",
            ("text", _) => family + "Text",
            _ => family,
        };
        return ThemeManager.Brush(key);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Percent (0..100) → arc of the ring gauge: circle r=50 around (60,60), clockwise from the top.</summary>
public sealed class ArcConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double pct = value is double d ? Math.Clamp(d, 0, 100) : 0;
        if (pct <= 0) return Geometry.Empty;
        const double r = 50, cx = 60, cy = 60;
        double a = Math.Min(pct, 99.99) / 100 * 2 * Math.PI;
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// User name → up to two initials for the avatar ("Іваненко Олена" → "ІО", "DOMAIN\o.ivanenko" → "OI");
/// a bare SID (orphan without a resolvable name) → "?".
/// </summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = (value as string ?? "").Trim();
        if (s.Length == 0 || s.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return "?";
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s[(slash + 1)..];
        var parts = s.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                     .Where(x => char.IsLetterOrDigit(x[0])).ToList();
        if (parts.Count == 0) return "?";
        var r = parts.Count == 1 ? parts[0][..Math.Min(2, parts[0].Length)] : $"{parts[0][0]}{parts[1][0]}";
        return r.ToUpper(c);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>User name → avatar colours (stable per name). Parameter "fg" = text colour, otherwise background.</summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    private static readonly (string bg, string fg)[] Pairs =
    {
        ("AccentSoft", "AccentText"), ("MountSoft", "MountText"), ("OkSoft", "OkText"), ("WarnSoft", "WarnText"), ("PinkSoft", "PinkText"),
    };

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var s = value as string ?? "";
        var pair = s.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || s.Length == 0
            ? ("NeutralSoft", "Muted")
            : Pairs[(int)(StableHash(s) % (uint)Pairs.Length)];
        return ThemeManager.Brush(Equals(p, "fg") ? pair.Item2 : pair.Item1);
    }

    /// <summary>FNV-1a (string.GetHashCode differs between runs).</summary>
    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s.ToUpperInvariant()) { h ^= ch; h *= 16777619; }
        return h;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>0..100 (percent) or 0..1 (fraction, parameter="1") → ScaleX 0..1.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        if (!Equals(p, "1")) v /= 100.0;
        return Math.Clamp(v, 0, 1);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null/empty/false/0 → Collapsed; parameter="!" inverts.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool on = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        if (Equals(p, "!")) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>values[0] (current filter) == values[1] (card Tag) → true. "All" card is lit only when nothing is filtered.</summary>
public sealed class FilterMatchConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length == 2 && values[0]?.ToString() == values[1]?.ToString();
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Enum value == parameter (for radio buttons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        v is true ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}
