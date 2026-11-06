using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FSLTool.Models;
using FSLTool.ViewModels;

namespace FSLTool.Views;

/// <summary>
/// Custom filter editor (button «Фільтр» next to the search box): a tree of conditions over any column / property,
/// groups with І / АБО / НЕ, named filters shared in filters.json. Changes apply to the table immediately.
/// </summary>
public partial class FilterWindow : Window
{
    private static FilterWindow? _open;
    private readonly MainViewModel _vm;
    private bool _loadingSaved;

    private FilterWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowChrome.Attach(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { if (_open == this) _open = null; };
        Loaded += (_, _) => { SavedBox.SelectedItem = vm.CurrentSavedName; UpdateDeleteButton(); };
    }

    /// <summary>Opens the editor (or brings the already open one to the front).</summary>
    public static void ShowEditor(Window owner, MainViewModel vm)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new FilterWindow(vm);
        if (owner.IsLoaded) _open.Owner = owner;
        else _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open.Show();
    }

    private static FilterGroup? GroupOf(object sender) => (sender as FrameworkElement)?.DataContext as FilterGroup;

    private void AddCondition_Click(object sender, RoutedEventArgs e) => GroupOf(sender)?.Items.Add(new FilterCondition());

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = new FilterGroup { Mode = GroupMode.Any };
        g.Items.Add(new FilterCondition());
        GroupOf(sender)?.Items.Add(g);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FilterNode { Parent: { } parent } node) parent.Items.Remove(node);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _vm.ResetCustomFilter();
        SetSavedSelection(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───── saved filters ─────

    private void SavedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDeleteButton();
        if (_loadingSaved || SavedBox.SelectedItem is not string name) return;
        _vm.LoadSavedFilter(name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialog.Prompt(Loc.T("Filter.Save.Title"), Loc.T("Filter.Save.Prompt"),
            _vm.CurrentSavedName ?? "", Loc.T("Filter.Save.Button"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_vm.SavedFilterNames.Contains(name, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(name, _vm.CurrentSavedName, StringComparison.OrdinalIgnoreCase) &&
            !Dialog.Confirm(Loc.T("Filter.Save.Title"), Loc.T("Filter.Save.Exists", name), Loc.T("Filter.Save.Overwrite"), kind: DialogKind.Warning))
            return;
        _vm.SaveCustomFilter(name);
        SetSavedSelection(_vm.CurrentSavedName);
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBox.SelectedItem is not string name) return;
        if (!Dialog.Confirm(Loc.T("Filter.Delete.Title"), Loc.T("Filter.Delete.Confirm", name),
                Loc.T("Filter.Delete.Button"), kind: DialogKind.Danger))
            return;
        _vm.DeleteSavedFilter(name);
        SetSavedSelection(null);
    }

    private void SetSavedSelection(string? name)
    {
        _loadingSaved = true;
        SavedBox.SelectedItem = name;
        _loadingSaved = false;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton() => DeleteSaved.IsEnabled = SavedBox.SelectedItem != null;
}
