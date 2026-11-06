using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>Settings, rules and caches live in %ProgramData%\FSLTool so every admin on the server shares them.</summary>
public static class Storage
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FSLTool");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DefaultRulesFile => Path.Combine(DataDir, "cleanup-rules.json");
    public static string SizeCacheFile => Path.Combine(DataDir, "vsize-cache.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), Json) ?? new();
        }
        catch (Exception ex) { Log.Warn("Не вдалося прочитати налаштування: " + ex.Message); }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), Encoding.UTF8);
        File.Move(tmp, SettingsFile, true);
    }

    public static string RulesPath(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.RulesFile) ? DefaultRulesFile : s.RulesFile!;

    /// <summary>Loads the rules; writes the embedded default file on first start.</summary>
    public static CleanupRuleSet LoadRules(AppSettings s)
    {
        var path = RulesPath(s);
        if (!File.Exists(path) && path == DefaultRulesFile)
        {
            Directory.CreateDirectory(DataDir);
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("FSLTool.cleanup-rules.json")!;
            using var f = File.Create(path);
            res.CopyTo(f);
        }
        var set = JsonSerializer.Deserialize<CleanupRuleSet>(File.ReadAllText(path), Json)
                  ?? throw new InvalidDataException("Порожній файл правил");
        foreach (var r in set.Rules)
            foreach (var p in r.Paths)
                if (p.Split('\\', '/').Any(seg => seg is ".." or "."))
                    throw new InvalidDataException($"Правило '{r.Name}': шлях '{p}' містить '..' або '.' — заборонено.");
        return set;
    }

    public static Dictionary<string, long> LoadSizeCache()
    {
        try
        {
            if (File.Exists(SizeCacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(SizeCacheFile))
                       is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    public static string AnalysisCacheFile => Path.Combine(DataDir, "analysis-cache.json");

    /// <summary>Compact (not indented) — the file holds a folder tree per container.</summary>
    private static readonly JsonSerializerOptions CacheJson = new(Json) { WriteIndented = false };
    private static readonly object _analysisLock = new();

    public static Dictionary<string, AnalysisRecord> LoadAnalysisCache() =>
        LoadShared<AnalysisRecord>(AnalysisCacheFile, _analysisLock, "Кеш аналізу");

    /// <summary>
    /// Re-reads the cache from disk (another admin or the headless job may have written to it),
    /// applies the change and saves it atomically. Returns the merged cache.
    /// </summary>
    public static Dictionary<string, AnalysisRecord> UpdateAnalysisCache(Action<Dictionary<string, AnalysisRecord>> change) =>
        UpdateShared(AnalysisCacheFile, _analysisLock, "Кеш аналізу", change);

    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    private static readonly object _historyLock = new();

    public static Dictionary<string, List<SizePoint>> LoadHistory() =>
        LoadShared<List<SizePoint>>(HistoryFile, _historyLock, "Історія розмірів");

    public static Dictionary<string, List<SizePoint>> UpdateHistory(Action<Dictionary<string, List<SizePoint>>> change) =>
        UpdateShared(HistoryFile, _historyLock, "Історія розмірів", change);

    // ── shared JSON dictionaries keyed by container path ──

    private static Dictionary<string, T> LoadShared<T>(string file, object gate, string what)
    {
        lock (gate)
            using (CrossProcessLock(file))
                return ReadShared<T>(file, what);
    }

    private static Dictionary<string, T> ReadShared<T>(string file, string what)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(file), CacheJson)
                       is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn($"{what}: файл пошкоджено, його буде перезаписано: {ex.Message}"); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, T> UpdateShared<T>(string file, object gate, string what, Action<Dictionary<string, T>> change)
    {
        lock (gate)
            using (CrossProcessLock(file))
            {
                var d = ReadShared<T>(file, what);
                change(d);
                try
                {
                    Directory.CreateDirectory(DataDir);
                    var tmp = file + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(d, CacheJson), Encoding.UTF8);
                    File.Move(tmp, file, true);
                }
                catch (Exception ex) { Log.Warn($"{what}: не вдалося зберегти: {ex.Message}"); }
                return d;
            }
    }

    /// <summary>
    /// The UI (any admin) and the scheduled headless job may update the same file at once:
    /// a machine-wide mutex serialises read-modify-write. If it can't be used, the in-process lock still applies.
    /// </summary>
    private static IDisposable? CrossProcessLock(string file)
    {
        try
        {
            var m = new Mutex(false, @"Global\FSLTool-" + Path.GetFileName(file));
            try
            {
                if (m.WaitOne(TimeSpan.FromSeconds(15))) return new MutexRelease(m);
            }
            catch (AbandonedMutexException) { return new MutexRelease(m); }   // previous owner died — we own it now
            m.Dispose();
        }
        catch (Exception ex) { Log.Warn($"Блокування {Path.GetFileName(file)} недоступне: {ex.Message}"); }
        return null;
    }

    private sealed class MutexRelease(Mutex m) : IDisposable
    {
        public void Dispose() { try { m.ReleaseMutex(); } catch { } m.Dispose(); }
    }

    public static void SaveSizeCache(Dictionary<string, long> d)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SizeCacheFile, JsonSerializer.Serialize(d));
        }
        catch { }
    }
}

/// <summary>Simple audit log: %ProgramData%\FSLTool\logs\fsltool-yyyyMMdd.log + live feed for the UI.</summary>
public static class Log
{
    private static readonly object _lock = new();
    public static event Action<DateTime, string, string>? Entry;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Storage.LogDir);
                File.AppendAllText(Path.Combine(Storage.LogDir, $"fsltool-{now:yyyyMMdd}.log"),
                    $"{now:yyyy-MM-dd HH:mm:ss} [{level}] [{Environment.UserName}] {msg}{Environment.NewLine}");
            }
            catch { }
        }
        Entry?.Invoke(now, level, msg);
    }
}
