using System.Diagnostics;

namespace FSLTool.Services;

/// <summary>
/// Progress of one operation on one container. The operation is split into weighted stages
/// (e.g. attach 0–5 %, cleanup 5–55 %, compact 60–100 %); inside a stage the caller reports 0..1.
/// Reports are throttled (~10/s) because cleanup can touch hundreds of thousands of files.
/// </summary>
public sealed class OpReporter
{
    public readonly record struct Snapshot(string Stage, double Overall, string? Detail, TimeSpan Elapsed);

    private readonly Action<Snapshot> _sink;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTicks = -1;
    private string _stage = "";
    private double _from, _to = 1, _overall;

    public OpReporter(Action<Snapshot> sink) => _sink = sink;

    /// <summary>A reporter that discards everything (for internal calls without UI).</summary>
    public static OpReporter None { get; } = new(_ => { });

    /// <summary>Starts a stage that occupies [from, to] of the whole operation (fractions 0..1).</summary>
    public void Stage(string name, double from, double to, string? detail = null)
    {
        _stage = name; _from = from; _to = to;
        Emit(from, detail, force: true);
    }

    /// <summary>Progress inside the current stage, 0..1. Null keeps the current value (detail-only update).</summary>
    public void Report(double? fraction, string? detail = null)
    {
        var overall = fraction is double f ? _from + (_to - _from) * Math.Clamp(f, 0, 1) : _overall;
        // the end of a stage is never throttled away, otherwise the bar could freeze below 100 %
        Emit(overall, detail, force: fraction >= 1);
    }

    private readonly object _gate = new();

    // the analysis walks folders on several threads, so reports may arrive concurrently
    private void Emit(double overall, string? detail, bool force)
    {
        Snapshot snap;
        lock (_gate)
        {
            _overall = Math.Max(_overall, overall); // never goes backwards
            var now = _clock.ElapsedTicks;
            if (!force && _lastTicks >= 0 && now - _lastTicks < Stopwatch.Frequency / 10) return;
            _lastTicks = now;
            snap = new Snapshot(_stage, _overall, detail, _clock.Elapsed);
        }
        _sink(snap);
    }
}
