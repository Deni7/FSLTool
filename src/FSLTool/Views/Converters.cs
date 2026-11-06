using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FSLTool.Models;

namespace FSLTool.Views;

/// <summary>Severity / MountState → brush from the theme.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Severity.Critical => (Brush)Application.Current.Resources["Crit"],
        Severity.Warning => (Brush)Application.Current.Resources["Warn"],
        Severity.Ok => (Brush)Application.Current.Resources["Ok"],
        MountState.InUseSmb or MountState.AttachedLocal => (Brush)Application.Current.Resources["Mount"],
        MountState.Locked => (Brush)Application.Current.Resources["Warn"],
        MountState.Free => (Brush)Application.Current.Resources["Ok"],
        _ => (Brush)Application.Current.Resources["Muted"],
    };
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

/// <summary>bool → GridLength; parameter "whenTrue|whenFalse" in pixels (e.g. "48|290" for the collapsible sidebar).</summary>
public sealed class BoolToLengthConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var parts = (p as string ?? "0|0").Split('|');
        var px = double.Parse(value is true ? parts[0] : parts[^1], CultureInfo.InvariantCulture);
        return new GridLength(px);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
