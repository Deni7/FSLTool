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
        var doc = MarkdownLite.Render(LoadText(), out var toc);
        Viewer.Document = doc;
        Toc.ItemsSource = toc;
        SourceInitialized += (_, _) => WindowChrome.UseDarkTitleBar(this);
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
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

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

    /// <summary>The embedded Docs/Help.md.</summary>
    public static string LoadText()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FSLTool.Docs.Help.md");
        if (s == null) return "# Довідка\n\nДовідку не знайдено в збірці.";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
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
            SearchStatus.Text = $"«{query}» не знайдено";
            return false;
        }
        var (start, end) = hit.Value;
        Viewer.Selection?.Select(start, end);
        _searchFrom = end;
        if (start.Paragraph is Paragraph p) ScrollToTop(p);
        SearchStatus.Text = "Enter — наступний збіг";
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
