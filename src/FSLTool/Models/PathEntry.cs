using FSLTool.ViewModels;

namespace FSLTool.Models;

/// <summary>A configured profile root with the free space of the volume / share it lives on.</summary>
public sealed class PathEntry(string path) : ObservableObject
{
    public string Path { get; } = path;

    private long? _total, _free;
    public long? Total { get => _total; private set => Set(ref _total, value); }
    public long? Free { get => _free; private set => Set(ref _free, value); }

    private long _containersBytes;
    private int _containersCount;
    /// <summary>Sum of container files found under this root by the last scan.</summary>
    public long ContainersBytes { get => _containersBytes; private set => Set(ref _containersBytes, value); }
    public int ContainersCount { get => _containersCount; private set => Set(ref _containersCount, value); }

    private string? _error;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public double? UsedPercent => Total is > 0 && Free is long f ? 100.0 * (Total.Value - f) / Total.Value : null;
    public bool HasSpace => UsedPercent != null;

    /// <summary>Thresholds are the same as for containers (warning / overfull, %).</summary>
    public Severity Severity => UsedPercent switch
    {
        double p when p >= ProfileInfo.CriticalPercent => Severity.Critical,
        double p when p >= ProfileInfo.WarnPercent => Severity.Warning,
        _ => Severity.Ok,
    };

    public string SpaceText => Error != null ? Loc.T("Path.Unavailable", Error)
        : HasSpace ? Loc.T("Path.FreeOf", Format.Bytes(Free!.Value), Format.Bytes(Total!.Value))
        : Loc.T("Path.Unknown");

    public string ContainersText => ContainersCount == 0 ? "" : Loc.T("Path.Containers", ContainersCount, Format.Bytes(ContainersBytes));

    /// <summary>Reads the free space of the volume (blocking; works for local paths and UNC shares).</summary>
    public (long? total, long? free, string? error) Query()
    {
        try
        {
            var (t, f) = Services.VolumeHelper.GetSpace(Path.EndsWith('\\') ? Path : Path + "\\");
            return (t, f, null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // the exception text is just the API name — show what Windows says
            return (null, null, new System.ComponentModel.Win32Exception(ex.NativeErrorCode).Message);
        }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    /// <summary>Applies a <see cref="Query"/> result and the container totals (UI thread).</summary>
    public void Apply((long? total, long? free, string? error) space, int count, long bytes)
    {
        Total = space.total;
        Free = space.free;
        Error = space.error;
        ContainersCount = count;
        ContainersBytes = bytes;
        Notify();
    }

    public void Notify()
    {
        OnPropertyChanged(nameof(UsedPercent));
        OnPropertyChanged(nameof(HasSpace));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(SpaceText));
        OnPropertyChanged(nameof(ContainersText));
    }
}
