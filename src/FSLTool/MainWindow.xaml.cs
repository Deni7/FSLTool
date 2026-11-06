using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FSLTool.Models;
using FSLTool.ViewModels;
using FSLTool.Views;

namespace FSLTool;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // always start maximized; the saved size is what "Restore down" returns to
        Width = _vm.Settings.WindowWidth;
        Height = _vm.Settings.WindowHeight;
        WindowState = WindowState.Maximized;

        WindowChrome.Attach(this);
        // F1 — built-in help (works wherever the focus is in the main window)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1) { HelpWindow.ShowHelp(this); e.Handled = true; }
            // Ctrl+Shift+F — custom filter editor
            else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { FilterWindow.ShowEditor(this, _vm); e.Handled = true; }
        };
        Loaded += async (_, _) => { if (_vm.Paths.Count > 0) await _vm.RefreshAsync(); };
        Closing += (_, _) =>
        {
            _vm.Settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _vm.Settings.WindowWidth = Width;
                _vm.Settings.WindowHeight = Height;
            }
            _vm.SaveSettings();
        };
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTypedPath_Click(sender, e);
    }

    private void AddTypedPath_Click(object sender, RoutedEventArgs e)
    {
        _vm.AddPathText(PathBox.Text);
        PathBox.Clear();
    }


    /// <summary>▾ of the refresh split button: open its menu under the button on left click.</summary>
    private void RefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = RefreshMenuButton.ContextMenu!;
        menu.DataContext = DataContext;
        menu.PlacementTarget = RefreshMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => HelpWindow.ShowHelp(this);

    /// <summary>Language pill in the header: a menu of the languages (the current choice is ticked).</summary>
    private void Language_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = LangButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var current = _vm.SelectedLanguage;
        foreach (var lang in _vm.Languages)
        {
            var item = new System.Windows.Controls.MenuItem { Header = lang.Name, IsChecked = lang == current };
            item.Click += (_, _) => _vm.SelectedLanguage = lang;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void CustomFilter_Click(object sender, RoutedEventArgs e) => FilterWindow.ShowEditor(this, _vm);

    /// <summary>
    /// The check-box column: the default first click would sort ascending (unchecked first); here the first click
    /// puts the CHECKED rows on top, the next one at the bottom. Inside each group the usual order (fill, desc) is kept.
    /// Rows are not re-sorted while you tick boxes — click the header again to regroup.
    /// </summary>
    private void Grid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
    {
        if (e.Column.SortMemberPath != nameof(ProfileInfo.IsChecked))
        {
            // another column takes over: the default sort does not clear our custom arrow
            foreach (var c in Grid.Columns)
                if (c.SortMemberPath == nameof(ProfileInfo.IsChecked)) c.SortDirection = null;
            return;
        }
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        var view = _vm.View;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(ProfileInfo.IsChecked), dir));
            view.SortDescriptions.Add(new SortDescription(nameof(ProfileInfo.UsagePercent), ListSortDirection.Descending));
        }
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
    }
}
