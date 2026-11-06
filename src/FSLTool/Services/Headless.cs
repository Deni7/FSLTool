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
    public const int ExitOk = 0, ExitSomeFailed = 1, ExitBadArgs = 2, ExitFatal = 3, ExitCancelled = 4, ExitAlreadyRunning = 5, ExitMailFailed = 6;

    private sealed class Options
    {
        public string Command = "";
        public string Filter = "all";
        public string? User;
        /// <summary>Name of a saved custom filter (filters.json); <see cref="WhereFilter"/> is loaded in Execute.</summary>
        public string? Where;
        public FilterGroup? WhereFilter;
        public double? MinGainGb;
        public int? MaxMinutes;
        public int? MaxCount;
        public bool DryRun;
        public bool Measure = true;
        public bool? CompactAfterClean;
        // email report
        public string? MailTo, MailOn, Smtp, MailFrom, SmtpUser, SmtpPasswordEnv;
        public bool SmtpSsl, NoMail;
    }

    /// <summary>Console help in the current language (Localization/cli.*.json, key Cli.Help).</summary>
    public static string Help => Loc.T("Cli.Help");

    /// <summary>Report of the current run; <see cref="Out"/> copies the console output into it.</summary>
    private static RunReport? _report;

    public static int Run(string[] args)
    {
        InitConsole();
        // nothing may block on a dialog without a window
        Dialog.Interceptor = req => { Log.Warn("[CLI] " + Loc.T("Cli.DialogSkipped", req.Title, req.Message)); return false; };

        if (!TryParse(args, out var o, out var error))
        {
            Out(error!);
            Out("");
            Out(Help);
            return ExitBadArgs;
        }
        if (o.Command == "help") { Out(Help); return ExitOk; }

        var settings = Storage.LoadSettings();
        if (!TryMailConfig(o, settings.Mail ?? new(), out var mail, out error))
        {
            Out(error!);
            return ExitBadArgs;
        }

        var report = _report = new RunReport
        {
            Command = o.Command,
            CommandLine = "FSLTool.exe " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            Filter = o.Filter + (o.Where != null ? Loc.T("Cli.Report.Where", o.Where) : ""), UserMask = o.User, DryRun = o.DryRun,
            MinGainGb = o.MinGainGb ?? settings.CompactMinGb, MaxMinutes = o.MaxMinutes, MaxCount = o.MaxCount,
        };
        Log.Entry += (t, lvl, msg) =>
        {
            Out($"{t:HH:mm:ss} {lvl,-5} {msg}");
            if (lvl is "WARN" or "ERROR") lock (report.Problems) report.Problems.Add((t, lvl, msg));
        };

        int code;
        using (var single = new Mutex(false, @"Global\FSLTool-headless"))
        {
            bool owned;
            try { owned = single.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                Log.Warn("[CLI] " + Loc.T("Cli.AlreadyRunning"));
                code = ExitAlreadyRunning;
            }
            else
            {
                using var cts = new CancellationTokenSource();
                try { Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); }; } catch { }
                Log.Info("[CLI] " + Loc.T("Cli.Start", report.CommandLine));
                try { code = Execute(o, settings, report, cts.Token); }
                catch (OperationCanceledException) { Log.Warn("[CLI] " + Loc.T("Cli.Cancelled")); code = ExitCancelled; }
                catch (Exception ex) { Log.Error("[CLI] " + Loc.T("Cli.Fatal", ex.ToString())); code = ExitFatal; }
                finally { try { single.ReleaseMutex(); } catch { } }
            }
        }

        report.ExitCode = code;
        report.Finished = DateTime.Now;
        return mail != null ? SendReport(mail, report, code) : code;
    }

    // ───────────────────────────── main flow ─────────────────────────────

    private static int Execute(Options o, AppSettings settings, RunReport r, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        if (settings.ProfilePaths.Count == 0)
        {
            Log.Error("[CLI] " + Loc.T("Cli.NoPaths"));
            return ExitBadArgs;
        }
        ProfileInfo.WarnPercent = settings.WarnPercent;
        ProfileInfo.CriticalPercent = settings.CriticalPercent;
        ProfileInfo.StaleDays = settings.StaleDays;
        double minGainGb = o.MinGainGb ?? settings.CompactMinGb;
        ProfileInfo.CompactMinBytes = (long)(Math.Max(0, minGainGb) * (1L << 30));

        // a missing or broken filter must stop the run before anything is touched (otherwise it would hit every container)
        if (o.Where != null && o.Command != "scan")
        {
            var saved = Storage.LoadFilters();
            if (!saved.TryGetValue(o.Where, out var wf))
            {
                Log.Error("[CLI] " + Loc.T("Cli.Where.NotFound", o.Where, Storage.FiltersFile,
                          saved.Count > 0 ? Loc.T("Cli.Where.Available", string.Join(", ", saved.Keys.Select(k => Loc.T("Cli.Where.Quoted", k))))
                                          : Loc.T("Cli.Where.NoneSaved")));
                return ExitBadArgs;
            }
            if (wf.BrokenConditions().FirstOrDefault() is { } bad)
            {
                Log.Error("[CLI] " + Loc.T("Cli.Where.BadCondition", o.Where, bad.Describe(), bad.Error));
                return ExitBadArgs;
            }
            if (!wf.IsActive)
            {
                Log.Error("[CLI] " + Loc.T("Cli.Where.Empty", o.Where));
                return ExitBadArgs;
            }
            o.WhereFilter = wf;
            Log.Info("[CLI] " + Loc.T("Cli.Where.Applied", o.Where, wf.Describe()));
        }

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
        r.Containers = list;
        foreach (var path in settings.ProfilePaths)
        {
            var e = new PathEntry(path);
            var mine = list.Where(p => p.RootPath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            e.Apply(e.Query(), mine.Count, mine.Sum(p => p.FileSize));
            r.Paths.Add(e);
        }

        Log.Info("[CLI] " + Loc.T("Cli.ScanSummary", list.Count, Format.Bytes(list.Sum(p => p.FileSize)),
                 list.Count(p => p.Severity == Severity.Critical), list.Count(p => p.IsMounted),
                 list.Count(p => p.IsOrphan), list.Count(p => p.IsDisabled), list.Count(p => p.EtaSoon)));
        if (o.Command == "scan") return ExitOk;

        // ── targets ──
        var targets = Select(list, o);
        r.Selected = targets.Count;
        int mounted = r.MountedSkipped = targets.Count(p => p.IsMounted);
        targets = targets.Where(p => !p.IsMounted).ToList();
        if (o.MaxCount is int max && targets.Count > max) targets = targets.Take(max).ToList();
        Log.Info("[CLI] " + Loc.T("Cli.Targets", o.Command, targets.Count,
                 mounted > 0 ? Loc.T("Cli.Targets.Mounted", mounted) : "",
                 o.Filter,
                 (o.User != null ? Loc.T("Cli.Targets.User", o.User) : "") + (o.Where != null ? Loc.T("Cli.Targets.Where", o.Where) : "")));

        if (o.DryRun)
        {
            foreach (var p in targets)
            {
                Out("  " + Describe(p, o));
                r.Items.Add(new ReportItem { Profile = p, Outcome = ItemOutcome.Planned, SizeBefore = p.FileSize, SizeAfter = p.FileSize, Message = Plan(p, o) });
            }
            Log.Info("[CLI] " + Loc.T("Cli.DryRunDone"));
            return ExitOk;
        }

        // ── run ──
        var deadline = o.MaxMinutes is int m ? DateTime.Now.AddMinutes(m) : (DateTime?)null;
        bool compactAfterClean = o.CompactAfterClean ?? settings.CompactAfterCleanup;

        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline is DateTime d && DateTime.Now >= d)
            {
                Log.Warn("[CLI] " + Loc.T("Cli.TimeLimit", o.MaxMinutes, targets.Count - i));
                r.NotStarted = targets.Count - i;
                break;
            }
            var p = targets[i];
            Out($"[{i + 1}/{targets.Count}] {p.DisplayUser} · {p.FileName} · {p.SizeText}");
            var item = new ReportItem { Profile = p, SizeBefore = p.FileSize };
            r.Items.Add(item);
            var clock = Stopwatch.StartNew();
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
                            if (!o.Measure) throw new OperationSkippedException(Loc.T("Cli.Skip.NoAnalysis"));
                            ProfileOperations.Measure(p, reporter, ct);
                        }
                        if (!p.WorthCompacting)
                            throw new OperationSkippedException(Loc.T("Cli.Skip.LowGain", p.ReclaimableText, minGainGb));
                        ProfileOperations.Compact(p, reporter, ct);
                        break;
                }
                item.Outcome = ItemOutcome.Ok;
                item.Message = p.LastResult ?? "";
                Log.Info($"[CLI]   {p.DisplayUser}: {p.LastResult}");
            }
            catch (OperationCanceledException)
            {
                item.Outcome = ItemOutcome.Failed;
                item.Message = Loc.T("Cli.Cancelled");
                throw;
            }
            catch (InvalidOperationException ex) when (p.IsMounted || ex is OperationSkippedException)
            {
                item.Outcome = ItemOutcome.Skipped;
                item.Message = ex.Message;
                Log.Warn("[CLI]   " + Loc.T("Cli.ItemSkipped", p.DisplayUser, ex.Message));
            }
            catch (Exception ex)
            {
                item.Outcome = ItemOutcome.Failed;
                item.Message = ex.Message;
                Log.Error($"[CLI]   {p.DisplayUser} ({p.FilePath}): {ex.Message}");
            }
            finally
            {
                item.SizeAfter = p.FileSize;
                item.Duration = clock.Elapsed;
                // keep what we learned (usage, folders, FS state) even if the step failed half-way
                analysis = ProfileStore.SaveAnalysis(new[] { p }, analysis) ?? analysis;
            }
        }

        Log.Info("[CLI] " + Loc.T("Cli.Summary", o.Command, r.Ok, r.Skipped, r.Failed,
                 r.Saved > 0 ? Loc.T("Cli.Summary.Saved", Format.Bytes(r.Saved)) : "",
                 total.Elapsed.ToString(@"h\:mm\:ss")));
        return r.Failed > 0 ? ExitSomeFailed : ExitOk;
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
        if (o.WhereFilter is { } wf) q = q.Where(wf.Matches);
        // most urgent / most rewarding first, so a time limit cuts the least useful work
        return (o.Command == "compact"
                ? q.OrderByDescending(p => p.Reclaimable ?? -1).ThenByDescending(p => p.FileSize)
                : q.OrderByDescending(p => p.UsagePercent ?? 0).ThenByDescending(p => p.FileSize))
            .ToList();
    }

    private static string Describe(ProfileInfo p, Options o) =>
        $"{p.DisplayUser,-28} {p.UsageText,6} {p.SizeText,10} / {p.MaxSizeText,-10}" +
        (o.Command == "compact"
            ? (p.UsageIsExact ? " " + Loc.T("Cli.Reclaims", p.ReclaimableText) + (p.WorthCompacting ? "" : Loc.T("Cli.Describe.Skip")) : Loc.T("Cli.Describe.WillMeasure"))
            : "") +
        (p.FlagsText.Length > 0 ? $"  [{p.FlagsText}]" : "");

    /// <summary>What a --dry-run would do with the container (for the report).</summary>
    private static string Plan(ProfileInfo p, Options o) =>
        (o.Command switch
        {
            "compact" => p.UsageIsExact
                ? Loc.T("Cli.Reclaims", p.ReclaimableText) + (p.WorthCompacting ? "" : Loc.T("Cli.Plan.WillSkip"))
                : Loc.T("Cli.Plan.Measure"),
            "clean" => p.Estimates is { Count: > 0 } ? Loc.T("Cli.Plan.CleanEstimate", p.EstimateTotalText) : Loc.T("Cli.Plan.Clean"),
            _ => Loc.T("Cli.Plan.Analyze"),
        }) + (p.FlagsText.Length > 0 ? $" · {p.FlagsText}" : "");

    // ───────────────────────────── email report ─────────────────────────────

    /// <summary>
    /// settings.json "mail" overridden by the command line. No recipients → no report (mail = null).
    /// Mail options without recipients, SMTP server or password are an error, reported before the run starts.
    /// </summary>
    private static bool TryMailConfig(Options o, MailSettings s, out MailConfig? mail, out string? error)
    {
        mail = null;
        error = null;
        if (o.NoMail) return true;
        bool explicitMail = o.MailTo != null || o.MailOn != null || o.Smtp != null || o.MailFrom != null ||
                            o.SmtpUser != null || o.SmtpPasswordEnv != null || o.SmtpSsl;
        var to = o.MailTo != null ? MailConfig.SplitAddresses(o.MailTo) : (s.To ?? new()).SelectMany(MailConfig.SplitAddresses).ToList();
        if (to.Count == 0)
        {
            if (explicitMail) error = Loc.T("Cli.Mail.NoRecipients");
            return error == null;
        }

        var c = new MailConfig { To = to, Ssl = o.SmtpSsl || s.Ssl, When = (o.MailOn ?? s.When ?? "always").Trim().ToLowerInvariant() };
        if (!MailConfig.WhenValues.Contains(c.When)) { error = Loc.T("Cli.Mail.BadWhen", string.Join(", ", MailConfig.WhenValues)); return false; }

        if (o.Smtp != null)
        {
            var hp = o.Smtp.Trim();
            int colon = hp.LastIndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(hp[(colon + 1)..], out var port) || port is <= 0 or > 65535) { error = Loc.T("Cli.Mail.BadPort"); return false; }
                c.Port = port;
                hp = hp[..colon];
            }
            c.Host = hp;
        }
        else
        {
            c.Host = s.SmtpHost?.Trim() ?? "";
            c.Port = s.SmtpPort is > 0 and <= 65535 ? s.SmtpPort : 25;
        }
        if (c.Host.Length == 0) { error = Loc.T("Cli.Mail.NoHost"); return false; }

        c.From = (o.MailFrom ?? s.From) is { Length: > 0 } f ? f.Trim() : MailConfig.DefaultFrom();
        foreach (var a in to.Append(c.From))
            if (!System.Net.Mail.MailAddress.TryCreate(a, out _)) { error = Loc.T("Cli.Mail.BadAddress", a); return false; }

        c.User = (o.SmtpUser ?? s.User) is { Length: > 0 } u ? u.Trim() : null;
        if (c.User != null)
        {
            var env = (o.SmtpPasswordEnv ?? s.PasswordEnv)?.Trim();
            if (string.IsNullOrEmpty(env)) { error = Loc.T("Cli.Mail.NoPasswordEnv"); return false; }
            c.Password = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(c.Password)) { error = Loc.T("Cli.Mail.PasswordNotSet", env); return false; }
        }
        mail = c;
        return true;
    }

    private static int SendReport(MailConfig mail, RunReport r, int code)
    {
        if (!mail.ShouldSend(r))
        {
            Log.Info("[CLI] " + Loc.T("Cli.Mail.NotSent", mail.When));
            return code;
        }
        try
        {
            ReportMailer.Send(mail, r);
            Log.Info("[CLI] " + Loc.T("Cli.Mail.Sent", string.Join(", ", mail.To), mail.Host, mail.Port.ToString()));
            return code;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) msg += " → " + inner.Message;
            Log.Error("[CLI] " + Loc.T("Cli.Mail.Failed", mail.Host, mail.Port.ToString(), msg));
            return code == ExitOk ? ExitMailFailed : code;
        }
    }

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

            // --lang xx / --lang=xx: already applied by App (Loc.Init) before Run — only validated here
            if (a.Equals("--lang", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                var code = a.Length > "--lang".Length ? a["--lang=".Length..] : Next();
                if (!Loc.IsSupported(code?.Trim())) { error = Loc.T("Cli.Arg.Lang", string.Join(", ", Loc.Available.Select(l => l.Code))); return false; }
                continue;
            }

            switch (key)
            {
                case "filter":
                    var f = Next()?.ToLowerInvariant();
                    if (f == null || !Filters.Contains(f)) { error = Loc.T("Cli.Arg.Filter", string.Join(", ", Filters)); return false; }
                    o.Filter = f;
                    break;
                case "user":
                    o.User = Next();
                    if (string.IsNullOrWhiteSpace(o.User)) { error = Loc.T("Cli.Arg.User"); return false; }
                    break;
                case "where":
                    o.Where = Next()?.Trim();
                    if (string.IsNullOrEmpty(o.Where)) { error = Loc.T("Cli.Arg.Where"); return false; }
                    break;
                case "min-gain":
                    if (!TryNumber(Next(), out var g) || g < 0) { error = Loc.T("Cli.Arg.MinGain"); return false; }
                    o.MinGainGb = g;
                    break;
                case "max-minutes":
                    if (!int.TryParse(Next(), out var mm) || mm <= 0) { error = Loc.T("Cli.Arg.MaxMinutes"); return false; }
                    o.MaxMinutes = mm;
                    break;
                case "max-count":
                    if (!int.TryParse(Next(), out var mc) || mc <= 0) { error = Loc.T("Cli.Arg.MaxCount"); return false; }
                    o.MaxCount = mc;
                    break;
                case "dry-run": o.DryRun = true; break;
                case "no-measure": o.Measure = false; break;
                case "no-compact": o.CompactAfterClean = false; break;
                case "mail-to":
                    o.MailTo = Next();
                    if (string.IsNullOrWhiteSpace(o.MailTo)) { error = Loc.T("Cli.Arg.MailTo"); return false; }
                    break;
                case "mail-on":
                    o.MailOn = Next()?.ToLowerInvariant();
                    if (o.MailOn == null || !MailConfig.WhenValues.Contains(o.MailOn)) { error = Loc.T("Cli.Arg.MailOn", string.Join(", ", MailConfig.WhenValues)); return false; }
                    break;
                case "smtp":
                    o.Smtp = Next();
                    if (string.IsNullOrWhiteSpace(o.Smtp)) { error = Loc.T("Cli.Arg.Smtp"); return false; }
                    break;
                case "mail-from":
                    o.MailFrom = Next();
                    if (string.IsNullOrWhiteSpace(o.MailFrom)) { error = Loc.T("Cli.Arg.MailFrom"); return false; }
                    break;
                case "smtp-user":
                    o.SmtpUser = Next();
                    if (string.IsNullOrWhiteSpace(o.SmtpUser)) { error = Loc.T("Cli.Arg.SmtpUser"); return false; }
                    break;
                case "smtp-password-env":
                    o.SmtpPasswordEnv = Next();
                    if (string.IsNullOrWhiteSpace(o.SmtpPasswordEnv)) { error = Loc.T("Cli.Arg.SmtpPasswordEnv"); return false; }
                    break;
                case "smtp-ssl": o.SmtpSsl = true; break;
                case "no-mail": o.NoMail = true; break;
                default:
                    error = Loc.T("Cli.Arg.Unknown", a);
                    return false;
            }
        }
        if (o.Command.Length == 0) { error = Loc.T("Cli.Arg.NoCommand"); return false; }
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
        {
            _report?.Output.AppendLine(s);
            try { Console.WriteLine(s); } catch { }
        }
    }
}
