using System.Windows;

namespace FSLTool.Views;

/// <summary>Attached properties used by the control templates in Themes/Theme.xaml.</summary>
public static class Ui
{
    /// <summary>Corner radius of buttons / text boxes (templates bind to it; default: pill-shaped 20).</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui),
        new FrameworkPropertyMetadata(new CornerRadius(20)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    /// <summary>Accent colour of a stat card (number and icon); the active card switches it to the text-on-primary colour.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
        "Accent", typeof(System.Windows.Media.Brush), typeof(Ui), new PropertyMetadata(null));

    public static System.Windows.Media.Brush? GetAccent(DependencyObject o) => (System.Windows.Media.Brush?)o.GetValue(AccentProperty);
    public static void SetAccent(DependencyObject o, System.Windows.Media.Brush? v) => o.SetValue(AccentProperty, v);

    /// <summary>Hint text shown in an empty text box (TextBox template in Theme.xaml).</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);

    /// <summary>Icon geometry (24×24 stroke icon) shown by templates that support it (search box, buttons).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(System.Windows.Media.Geometry), typeof(Ui), new PropertyMetadata(null));

    public static System.Windows.Media.Geometry? GetIcon(DependencyObject o) => (System.Windows.Media.Geometry?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, System.Windows.Media.Geometry? v) => o.SetValue(IconProperty, v);
}
