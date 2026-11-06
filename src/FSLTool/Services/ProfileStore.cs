using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>
/// Persistent per-container data shared by the UI and the headless mode:
/// the analysis cache and the daily size history.
/// </summary>
public static class ProfileStore
{
    /// <summary>History older than this is dropped.</summary>
    public const int KeepHistoryDays = 400;

    // ───────────── analysis cache ─────────────

    /// <summary>Applies cached analyses to containers that have none yet and whose file size is unchanged.</summary>
    public static void ApplyCachedAnalysis(IEnumerable<ProfileInfo> profiles, IReadOnlyDictionary<string, AnalysisRecord> cache)
    {
        foreach (var p in profiles)
            if (p.AnalyzedAt == null && cache.TryGetValue(p.FilePath, out var rec) && rec.FileSize == p.FileSize)
                p.ApplyAnalysis(rec);
    }

    /// <summary>
    /// Writes the analysis state of these containers to the shared cache (deleted ones are removed).
    /// Returns the merged cache, or null when there was nothing to write.
    /// </summary>
    public static Dictionary<string, AnalysisRecord>? SaveAnalysis(IEnumerable<ProfileInfo> profiles,
                                                                   IReadOnlyDictionary<string, AnalysisRecord>? known)
    {
        var changes = profiles.Select(p => (p.FilePath, rec: p.IsDeleted ? null : p.ToAnalysisRecord())).ToList();
        if (changes.All(c => c.rec == null && known?.ContainsKey(c.FilePath) != true)) return null;
        return Storage.UpdateAnalysisCache(d =>
        {
            foreach (var (path, rec) in changes)
                if (rec != null) d[path] = rec; else d.Remove(path);
        });
    }

    // ───────────── size history ─────────────

    /// <summary>
    /// Records today's size of every container (one point per day — a later scan the same day replaces it),
    /// drops points older than <see cref="KeepHistoryDays"/> and attaches the history to the containers.
    /// </summary>
    public static Dictionary<string, List<SizePoint>> RecordHistory(IReadOnlyList<ProfileInfo> profiles)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var oldest = today.AddDays(-KeepHistoryDays);
        var snapshot = profiles.Where(p => !p.IsDeleted && p.FileSize > 0)
            .Select(p => (p.FilePath, point: new SizePoint { Day = today, Size = p.FileSize, Used = p.UsageIsExact ? p.VolumeUsed : null }))
            .ToList();

        var all = Storage.UpdateHistory(d =>
        {
            foreach (var (path, point) in snapshot)
            {
                if (!d.TryGetValue(path, out var list)) d[path] = list = new List<SizePoint>();
                if (list.Count > 0 && list[^1].Day == today) list[^1] = point;
                else list.Add(point);
            }
            foreach (var key in d.Keys.ToList())
            {
                var list = d[key];
                list.RemoveAll(x => x.Day < oldest);
                if (list.Count == 0) d.Remove(key);
            }
        });
        Attach(profiles, all);
        return all;
    }

    /// <summary>Attaches already loaded history (no write).</summary>
    public static void Attach(IEnumerable<ProfileInfo> profiles, IReadOnlyDictionary<string, List<SizePoint>> history)
    {
        foreach (var p in profiles)
            p.SetHistory(history.TryGetValue(p.FilePath, out var h) ? h.OrderBy(x => x.Day).ToList() : null);
    }

    /// <summary>Removes the history of deleted containers.</summary>
    public static void ForgetHistory(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count > 0) Storage.UpdateHistory(d => { foreach (var p in list) d.Remove(p); });
    }
}
