using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FSLTool.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Tells WPF that every property may have changed (e.g. after the UI language changed).</summary>
    protected void RefreshAllBindings() => OnPropertyChanged(string.Empty);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>ObservableCollection that can replace its contents with a single Reset notification
/// (adding 500+ rows one by one makes the sorted view and the DataGrid refresh 500+ times).</summary>
public sealed class BulkObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task>? _async;
    private readonly Action<object?>? _sync;
    private readonly Func<object?, bool>? _can;
    private bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _sync = execute; _can = canExecute; }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    { _async = execute; _can = canExecute; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_sync != null) { _sync(parameter); return; }
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _async!(parameter); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
