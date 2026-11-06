using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FSLTool.Models;
using FSLTool.Native;
using FSLTool.Views;

namespace FSLTool.Services;

/// <summary>
/// Command-line mode for Task Scheduler (no window): FSLTool.exe scan|analyze|compact|clean [options].
/// Uses the same settings, rules, caches, history and safety checks as the UI; every action goes to the audit log.
/// </summary>
public static class Headless
{
    public const int ExitOk = 0, ExitSomeFailed = 1, ExitBadArgs = 2, ExitFatal = 3, ExitCancelled = 4, ExitAlreadyRunning = 5;

    private sealed class Options
    {
        public string Command = "";
        public string Filter = "all";
        public string? User;
        public double? MinGainGb;
        public int? MaxMinutes;
        public int? MaxCount;
        public bool DryRun;
        public bool Measure = true;
        public bool? CompactAfterClean;
    }

    public const string Help = """
        FSLTool — режим без вікна (для Планувальника завдань)

          FSLTool.exe <команда> [параметри]

        Команди:
          scan       сканування: стан, облікові записи, AD, запис історії розмірів
          analyze    аналіз вільних контейнерів (лише читання) — оновлює кеш аналізу
          compact    стиснення вільних контейнерів, які стиснення зменшить щонайменше на --min-gain
          clean      очищення за правилами (+ стиснення, якщо увімкнено в налаштуваннях)

        Параметри:
          --filter <f>        all | problem | critical | stale | orphan | disabled | compactable  (типово all)
          --user <маска>      лише користувачі / файли за маскою, напр. "CONTOSO\a*" або "*jdoe*"
          --min-gain <GB>     для compact: мінімальний виграш (типово — поріг із налаштувань)
          --no-measure        для compact: не вимірювати контейнери без даних аналізу (вони пропускаються)
          --no-compact        для clean: не стискати після очищення
          --max-minutes <N>   не починати новий контейнер після N хвилин (нічне вікно)
          --max-count <N>     обробити не більше N контейнерів
          --dry-run           лише показати, що буде зроблено

        Підмонтовані контейнери завжди пропускаються. Налаштування, правила та кеші — ті самі,
        що й у вікні (%ProgramData%\FSLTool). Журнал: %ProgramData%\FSLTool\logs.

        Коди виходу: 0 — успішно; 1 — були помилки; 2 — неправильні параметри; 3 — критична помилка;
                     4 — скасовано; 5 — інший екземпляр уже працює.

        У cmd запускайте через «start /wait FSLTool.exe …», щоб дочекатися завершення й побачити вивід.
        """;

    public static int Run(string[] args)
    {
        InitConsole();
        // nothing may block on a dialog without a window
        Dialog.Interceptor = req => { Log.Warn($"[CLI] діалог «{req.Title}» пропущено: {req.Message}"); return false; };

        if (!TryParse(args, out var o, out var error))
        {
            Out(error!);
            Out("");
            Out(Help);
            return ExitBadArgs;
        }
        if (o.Command == "help") { Out(Help); return ExitOk; }

        using var single = new Mutex(false, @"Global\FSLTool-headless");
        bool owned;
        try { owned = single.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            Out("Інший екземпляр FSLTool у режимі без вікна вже працює.");
            return ExitAlreadyRunning;
        }

        using var cts = new CancellationTokenSource();
        try { Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; } catch { }
        Log.Entry += (t, lvl, msg) => Out($"{t:HH:mm:ss} {lvl,-5} {msg}");
        Log.Info($"[CLI] запуск: FSLTool.exe {string.Join(' ', args)}");
        try { return Execute(o, cts.Token); }
        catch (OperationCanceledException) { Log.Warn("[CLI] скасовано"); return ExitCancelled; }
        catch (Exception ex) { Log.Error("[CLI] критична помилка: " + ex); return ExitFatal; }
        finally { try { single.ReleaseMutex(); } catch { } }
    }

    // ───────────────────────────── main flow ─────────────────────────────

    private static int Execute(Options o, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var settings = Storage.LoadSettings();
        if (settings.ProfilePaths.Count == 0)
        {
            Log.Error("[CLI] шляхи до профілів не налаштовано — додайте їх у вікні програми");
            return ExitBadArgs;
        }
        ProfileInfo.WarnPercent = settings.WarnPercent;
        ProfileInfo.CriticalPercent = settings.CriticalPercent;
        ProfileInfo.StaleDays = settings.StaleDays;
        double minGainGb = o.MinGainGb ?? settings.CompactMinGb;
        ProfileInfo.CompactMinBytes = (long)(Math.Max(0, minGainGb) * (1L << 30));

        CleanupRuleSet rules = new();
        if (o.Command is "analyze" or "clean")
            rules = Storage.LoadRules(settings);   // a broken rules file must stop the run

        // ── scan: same phases as the window ──
        var sizeCache = Storage.LoadSizeCache();
        var list = ProfileScanner.Discover(settings.ProfilePaths, sizeCache, ct);
        var analysis = Storage.LoadAnalysisCache();
        ProfileStore.ApplyCachedAnalysis(list, analysis);
        ProfileScanner.ReadHeaders(list, sizeCache, null, ct);
        Storage.SaveSizeCache(sizeCache);
        ProfileScanner.UpdateStates(list, ct);
        ProfileScanner.ResolveAccounts(list, ct);
        AdService.Resolve(list, ct);
        ProfileStore.RecordHistory(list);

        Log.Info($"[CLI] сканування: {list.Count} контейнерів, {Format.Bytes(list.Sum(p => p.FileSize))}; " +
                 $"переповнених {list.Count(p => p.Severity == Severity.Critical)}, підмонтованих {list.Count(p => p.IsMounted)}, " +
                 $"сиріт {list.Count(p => p.IsOrphan)}, вимкнених в AD {list.Count(p => p.IsDisabled)}, " +
                 $"межа ≤ 30 дн.: {list.Count(p => p.EtaSoon)}");
        if (o.Command == "scan") return ExitOk;

        // ── targets ──
        var targets = Select(list, o);
        int mounted = targets.Count(p => p.IsMounted);
        targets = targets.Where(p => !p.IsMounted).ToList();
        if (o.MaxCount is int max && targets.Count > max) targets = targets.Take(max).ToList();
        Log.Info($"[CLI] {o.Command}: до обробки {targets.Count}" + (mounted > 0 ? $", підмонтовані пропущено: {mounted}" : "") +
                 $" (фільтр {o.Filter}" + (o.User != null ? $", користувач {o.User}" : "") + ")");

        if (o.DryRun)
        {
            foreach (var p in targets) Out("  " + Describe(p, o));
            Log.Info("[CLI] --dry-run: нічого не змінено");
            return ExitOk;
        }

        // ── run ──
        var deadline = o.MaxMinutes is int m ? DateTime.Now.AddMinutes(m) : (DateTime?)null;
        int ok = 0, skipped = 0, failed = 0;
        long saved = 0;
        bool compactAfterClean = o.CompactAfterClean ?? settings.CompactAfterCleanup;

        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline is DateTime d && DateTime.Now >= d)
            {
                Log.Warn($"[CLI] ліміт часу {o.MaxMinutes} хв вичерпано — не оброблено {targets.Count - i}");
                break;
            }
            var p = targets[i];
            Out($"[{i + 1}/{targets.Count}] {p.DisplayUser} · {p.FileName} · {p.SizeText}");
            long before = p.FileSize;
            var reporter = StageReporter();
            try
            {
                switch (o.Command)
                {
                    case "analyze":
                        ProfileOperations.Analyze(p, rules, reporter, ct);
                        break;
                    case "clean":
                        ProfileOperations.Clean(p, rules, compactAfterClean, reporter, ct);
                        break;
                    case "compact":
                        if (!p.UsageIsExact)
                        {
                            if (!o.Measure) throw new OperationSkippedException("немає даних аналізу (--no-measure)");
                            ProfileOperations.Measure(p, reporter, ct);
                        }
                        if (!p.WorthCompacting)
                            throw new OperationSkippedException($"стиснення поверне лише {p.ReclaimableText} (< {minGainGb:0.#} GB)");
                        ProfileOperations.Compact(p, reporter, ct);
                        break;
                }
                ok++;
                saved += Math.Max(0, before - p.FileSize);
                Log.Info($"[CLI]   {p.DisplayUser}: {p.LastResult}");
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) when (p.IsMounted || ex is OperationSkippedException)
            {
                skipped++;
                Log.Warn($"[CLI]   {p.DisplayUser}: пропущено — {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"[CLI]   {p.DisplayUser} ({p.FilePath}): {ex.Message}");
            }
            finally
            {
                // keep what we learned (usage, folders, FS state) even if the step failed half-way
                analysis = ProfileStore.SaveAnalysis(new[] { p }, analysis) ?? analysis;
            }
        }

        Log.Info($"[CLI] {o.Command}: успішно {ok}, пропущено {skipped}, помилок {failed}" +
                 (saved > 0 ? $", файли зменшились на {Format.Bytes(saved)}" : "") +
                 $"; усього {total.Elapsed:h\\:mm\\:ss}");
        return failed > 0 ? ExitSomeFailed : ExitOk;
    }

    private static List<ProfileInfo> Select(List<ProfileInfo> list, Options o)
    {
        IEnumerable<ProfileInfo> q = o.Filter switch
        {
            "problem" => list.Where(p => p.Severity != Severity.Ok),
            "critical" => list.Where(p => p.Severity == Severity.Critical),
            "stale" => list.Where(p => p.IsStale),
            "orphan" => list.Where(p => p.IsOrphan),
            "disabled" => list.Where(p => p.IsDisabled),
            "compactable" => list.Where(p => p.WorthCompacting),
            _ => list,
        };
        if (o.User != null)
        {
            var rx = new Regex("^" + Regex.Escape(o.User).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
            q = q.Where(p => rx.IsMatch(p.DisplayUser) || rx.IsMatch(p.UserName) || rx.IsMatch(p.FileName));
        }
        // most urgent / most rewarding first, so a time limit cuts the least useful work
        return (o.Command == "compact"
                ? q.OrderByDescending(p => p.Reclaimable ?? -1).ThenByDescending(p => p.FileSize)
                : q.OrderByDescending(p => p.UsagePercent ?? 0).ThenByDescending(p => p.FileSize))
            .ToList();
    }

    private static string Describe(ProfileInfo p, Options o) =>
        $"{p.DisplayUser,-28} {p.UsageText,6} {p.SizeText,10} / {p.MaxSizeText,-10}" +
        (o.Command == "compact"
            ? (p.UsageIsExact ? $" стиснення поверне {p.ReclaimableText}" + (p.WorthCompacting ? "" : " — пропуск") : " — буде виміряно")
            : "") +
        (p.FlagsText.Length > 0 ? $"  [{p.FlagsText}]" : "");

    /// <summary>Prints each new stage of an operation (the detailed progress would flood a log file).</summary>
    private static OpReporter StageReporter()
    {
        string? last = null;
        return new OpReporter(s =>
        {
            if (s.Stage == last) return;
            last = s.Stage;
            Out($"    · {s.Stage}");
        });
    }

    // ───────────────────────────── arguments ─────────────────────────────

    private static readonly string[] Commands = { "scan", "analyze", "compact", "clean" };
    private static readonly string[] Filters = { "all", "problem", "critical", "stale", "orphan", "disabled", "compactable" };

    /// <summary>True when the process was started with arguments meant for the headless mode.</summary>
    public static bool IsHeadless(string[] args) => args.Length > 0;

    private static bool TryParse(string[] args, out Options o, out string? error)
    {
        o = new Options();
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].Trim();
            var key = a.TrimStart('-', '/').ToLowerInvariant();
            string? Next()
            {
                if (i + 1 < args.Length) return args[++i];
                return null;
            }

            if (key is "?" or "h" or "help") { o.Command = "help"; return true; }
            if (o.Command.Length == 0 && Commands.Contains(key)) { o.Command = key; continue; }

            switch (key)
            {
                case "filter":
                    var f = Next()?.ToLowerInvariant();
                    if (f == null || !Filters.Contains(f)) { error = $"--filter: очікується одне з {string.Join(", ", Filters)}"; return false; }
                    o.Filter = f;
                    break;
                case "user":
                    o.User = Next();
                    if (string.IsNullOrWhiteSpace(o.User)) { error = "--user: потрібна маска"; return false; }
                    break;
                case "min-gain":
                    if (!TryNumber(Next(), out var g) || g < 0) { error = "--min-gain: потрібне число GB, напр. 2 або 0.5"; return false; }
                    o.MinGainGb = g;
                    break;
                case "max-minutes":
                    if (!int.TryParse(Next(), out var mm) || mm <= 0) { error = "--max-minutes: потрібне ціле число хвилин"; return false; }
                    o.MaxMinutes = mm;
                    break;
                case "max-count":
                    if (!int.TryParse(Next(), out var mc) || mc <= 0) { error = "--max-count: потрібне ціле число"; return false; }
                    o.MaxCount = mc;
                    break;
                case "dry-run": o.DryRun = true; break;
                case "no-measure": o.Measure = false; break;
                case "no-compact": o.CompactAfterClean = false; break;
                default:
                    error = $"Невідомий параметр «{a}».";
                    return false;
            }
        }
        if (o.Command.Length == 0) { error = "Не вказано команду (scan, analyze, compact або clean)."; return false; }
        return true;
    }

    private static bool TryNumber(string? s, out double v) =>
        double.TryParse(s?.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    // ───────────────────────────── console ─────────────────────────────

    private static void InitConsole()
    {
        // a WinExe has no console: write to the caller's one (cmd / PowerShell); redirected output works anyway
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
    }

    private static readonly object _out = new();

    private static void Out(string s)
    {
        lock (_out)
            try { Console.WriteLine(s); } catch { }
    }
}
