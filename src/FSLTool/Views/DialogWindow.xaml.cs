using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FSLTool.Views;

public enum DialogKind { Info, Question, Warning, Danger, Error }

/// <summary>A dialog request (also what tests see when they intercept dialogs).</summary>
public sealed record DialogRequest(string Title, string Message, DialogKind Kind, string YesText, string? NoText)
{
    /// <summary>Non-null = show a text box with this initial value (Dialog.Prompt).</summary>
    public string? Input { get; init; }
    public bool Multiline { get; init; }
}

/// <summary>Themed replacement for MessageBox.</summary>
public partial class DialogWindow : Window
{
    private readonly DialogRequest _req;

    public DialogWindow(DialogRequest req)
    {
        InitializeComponent();
        _req = req;
        Title = req.Title;
        TitleText.Text = req.Title;
        MessageText.Text = req.Message;

        var res = Application.Current.Resources;
        (string glyph, string brush) = req.Kind switch
        {
            DialogKind.Question => ("?", "Accent"),
            DialogKind.Warning => ("!", "Warn"),
            DialogKind.Danger => ("!", "Crit"),
            DialogKind.Error => ("✕", "Crit"),
            _ => ("i", "Accent"),
        };
        IconGlyph.Text = glyph;
        // soft badge + coloured glyph from the current palette (Accent / Warn / Crit + Soft / Text)
        IconBadge.SetResourceReference(Border.BackgroundProperty, brush + "Soft");
        IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush + "Text");

        YesButton.Content = req.YesText;
        YesButton.Style = (Style)res[req.Kind == DialogKind.Danger ? "DangerButton" : "PrimaryButton"];
        if (req.NoText == null) NoButton.Visibility = Visibility.Collapsed;
        else NoButton.Content = req.NoText;

        // destructive actions: Enter must NOT confirm — the safe button is the default
        bool safeDefault = req.Kind == DialogKind.Danger && req.NoText != null;
        (safeDefault ? NoButton : YesButton).IsDefault = true;
        Loaded += (_, _) => (safeDefault ? NoButton : YesButton).Focus();

        if (req.Input != null)
        {
            InputBox.Visibility = Visibility.Visible;
            InputBox.Text = req.Input;
            if (req.Multiline)
            {
                InputBox.AcceptsReturn = true;
                InputBox.TextWrapping = TextWrapping.Wrap;
                InputBox.MinHeight = 70;
            }
            Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        }
    }

    public string InputText => InputBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

/// <summary>Entry points used everywhere instead of MessageBox.</summary>
public static class Dialog
{
    /// <summary>Tests can answer dialogs automatically; return value = "Yes"/OK pressed.</summary>
    public static Func<DialogRequest, bool>? Interceptor { get; set; }

    /// <summary>Yes/No question; null button texts = Common.Yes / Common.Cancel in the current language.</summary>
    public static bool Confirm(string title, string message, string? yes = null, string? no = null,
                               DialogKind kind = DialogKind.Question) =>
        Show(new DialogRequest(title, message, kind, yes ?? Loc.T("Common.Yes"), no ?? Loc.T("Common.Cancel")));

    public static void Info(string title, string message) => Show(new DialogRequest(title, message, DialogKind.Info, Loc.T("Common.Ok"), null));
    public static void Error(string title, string message) => Show(new DialogRequest(title, message, DialogKind.Error, Loc.T("Common.Ok"), null));

    /// <summary>Asks for a line (or block) of text; null = cancelled. Null <paramref name="yes"/> = Common.Ok.</summary>
    public static string? Prompt(string title, string message, string initial, string? yes = null, bool multiline = false)
    {
        var req = new DialogRequest(title, message, DialogKind.Question, yes ?? Loc.T("Common.Ok"), Loc.T("Common.Cancel")) { Input = initial, Multiline = multiline };
        string? text = null;
        return Show(req, w => text = w.InputText) ? text ?? initial : null;
    }

    private static bool Show(DialogRequest req, Action<DialogWindow>? onOk = null)
    {
        if (Interceptor != null) return Interceptor(req);
        var app = Application.Current;
        if (app?.Dispatcher.CheckAccess() == false)
            return app.Dispatcher.Invoke(() => Show(req, onOk));
        var w = new DialogWindow(req);
        var owner = app?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? app?.MainWindow;
        if (owner != null && owner != w && owner.IsLoaded) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (w.ShowDialog() != true) return false;
        onOk?.Invoke(w);
        return true;
    }
}
