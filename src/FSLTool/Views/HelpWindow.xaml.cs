using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace FSLTool.Views;

/// <summary>Built-in documentation (Docs/Help.md, embedded in the exe): contents, search, F1 from the main window.</summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _open;
    private TextPointer? _searchFrom;

    public HelpWindow()
    {
        InitializeComponent();
        Render();
        WindowChrome.Attach(this);
        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchFrom = null;
            SearchStatus.Text = "";
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };
        // language switched while the help is open: show the help in the new language
        Loc.Changed += OnLanguageChanged;
        // the document's colours are fixed when it is built: rebuild it in the new theme
        ThemeManager.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            Loc.Changed -= OnLanguageChanged;
            ThemeManager.Changed -= OnLanguageChanged;
            if (_open == this) _open = null;
        };
    }

    private void OnLanguageChanged() => Render();

    /// <summary>(Re)builds the document and the contents from the help in the current language.</summary>
    private void Render()
    {
        var doc = MarkdownLite.Render(LoadText(), out var toc);
        Viewer.Document = doc;
        Toc.ItemsSource = toc;
        _searchFrom = null;
        SearchStatus.Text = "";
    }

    /// <summary>Re-renders the open help window (if any), e.g. after the UI language changed.</summary>
    public static void Reload() => _open?.Render();

    /// <summary>Opens the help (or brings the already open one to the front).</summary>
    public static void ShowHelp(Window? owner)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new HelpWindow();
        if (owner is { IsLoaded: true }) _open.Owner = owner;
        else _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
    }

    /// <summary>The embedded help in the current language (→ English → the Ukrainian Docs/Help.md).</summary>
    public static string LoadText()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { Loc.HelpResource(Loc.Lang), Loc.HelpResource("en"), "FSLTool.Docs.Help.md" })
        {
            if (name == null) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        return Loc.T("HelpWin.NotFound");
    }

    private void Toc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Toc.SelectedItem is MarkdownLite.Heading h) ScrollToTop(h.Block);
    }

    /// <summary>
    /// BringIntoView scrolls as little as possible (a heading below would end up at the bottom edge):
    /// jump to the end first, so scrolling back up places the element at the top.
    /// </summary>
    private void ScrollToTop(FrameworkContentElement element)
    {
        if (Viewer.Template?.FindName("PART_ContentHost", Viewer) is ScrollViewer sv)
        {
            sv.ScrollToEnd();
            Viewer.UpdateLayout();
        }
        element.BringIntoView();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) _searchFrom = null;
        Find(SearchBox.Text);
        e.Handled = true;
    }

    /// <summary>Finds the next occurrence (case-insensitive, wraps around), selects it and scrolls to it.</summary>
    public bool Find(string query)
    {
        query = query.Trim();
        if (query.Length == 0 || Viewer.Document is not FlowDocument doc) return false;

        var hit = FindFrom(_searchFrom ?? doc.ContentStart, query) ?? FindFrom(doc.ContentStart, query);
        if (hit == null)
        {
            SearchStatus.Text = Loc.T("HelpWin.NotFoundQuery", query);
            return false;
        }
        var (start, end) = hit.Value;
        Viewer.Selection?.Select(start, end);
        _searchFrom = end;
        if (start.Paragraph is Paragraph p) ScrollToTop(p);
        SearchStatus.Text = Loc.T("HelpWin.NextMatch");
        return true;
    }

    private static (TextPointer, TextPointer)? FindFrom(TextPointer from, string query)
    {
        for (var p = from; p != null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var text = p.GetTextInRun(LogicalDirection.Forward);
            int i = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (i < 0) continue;
            var s = p.GetPositionAtOffset(i);
            var e = s?.GetPositionAtOffset(query.Length);
            if (s != null && e != null) return (s, e);
        }
        return null;
    }
}
