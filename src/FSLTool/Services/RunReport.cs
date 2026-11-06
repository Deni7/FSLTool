using System.Globalization;
using System.Net;
using System.Text;
using FSLTool.Models;

namespace FSLTool.Services;

public enum ItemOutcome { Ok, Skipped, Failed, Planned }

/// <summary>What happened to one container during a headless run.</summary>
public sealed class ReportItem
{
    public required ProfileInfo Profile { get; init; }
    public ItemOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
    public long SizeBefore { get; init; }
    public long SizeAfter { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Everything the headless run learned, collected for the email report: scan summary, storage, per-container results,
/// warnings / errors of the log and the whole console output (attached to the mail).
/// </summary>
public sealed class RunReport
{
    public string Command { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string Filter { get; set; } = "all";
    public string? UserMask { get; set; }
    public bool DryRun { get; set; }
    public double? MinGainGb { get; set; }
    public int? MaxMinutes { get; set; }
    public int? MaxCount { get; set; }

    public DateTime Started { get; } = DateTime.Now;
    public DateTime Finished { get; set; }
    public int ExitCode { get; set; }

    /// <summary>All containers after the scan (null if the run stopped before / during the scan).</summary>
    public List<ProfileInfo>? Containers { get; set; }
    public List<PathEntry> Paths { get; } = new();

    public int Selected { get; set; }
    public int MountedSkipped { get; set; }
    /// <summary>Targets not started because --max-minutes ran out.</summary>
    public int NotStarted { get; set; }
    public List<ReportItem> Items { get; } = new();

    public List<(DateTime Time, string Level, string Message)> Problems { get; } = new();
    public StringBuilder Output { get; } = new();

    public int Ok => Items.Count(i => i.Outcome == ItemOutcome.Ok);
    public int Skipped => Items.Count(i => i.Outcome == ItemOutcome.Skipped);
    public int Failed => Items.Count(i => i.Outcome == ItemOutcome.Failed);
    public long Saved => Items.Where(i => i.Outcome == ItemOutcome.Ok).Sum(i => Math.Max(0, i.SizeBefore - i.SizeAfter));
    public bool HasOperation => Command != "scan";

    /// <summary>Something an admin should look at: failure, skipped containers, overfull containers / storage, limit soon.</summary>
    public bool HasProblems =>
        ExitCode != Headless.ExitOk || Skipped > 0 || NotStarted > 0 ||
        Containers?.Any(p => p.Severity == Severity.Critical || p.EtaSoon) == true ||
        Paths.Any(p => p.Severity == Severity.Critical);

    public string StatusText => ExitCode switch
    {
        Headless.ExitOk => Loc.T(DryRun ? "Report.Status.DryRun" : "Report.Status.Ok"),
        Headless.ExitSomeFailed => Loc.T("Report.Status.SomeFailed"),
        Headless.ExitBadArgs => Loc.T("Report.Status.BadArgs"),
        Headless.ExitCancelled => Loc.T("Report.Status.Cancelled"),
        Headless.ExitAlreadyRunning => Loc.T("Report.Status.AlreadyRunning"),
        _ => Loc.T("Report.Status.Fatal"),
    };

    public string Subject
    {
        get
        {
            var s = new StringBuilder($"[FSLTool] {Environment.MachineName} · {Command}: {StatusText}");
            if (HasOperation && !DryRun && Items.Count > 0)
                s.Append(Loc.T("Report.Subject.Results", Ok, Skipped, Failed));
            else if (Containers != null)
                s.Append(Loc.T("Report.Subject.Containers", Containers.Count, Containers.Count(p => p.Severity == Severity.Critical)));
            return s.ToString();
        }
    }

    // ───────────────────────────── plain text (alternative view) ─────────────────────────────

    public string ToText()
    {
        var s = new StringBuilder();
        s.AppendLine(Loc.T("Report.Text.Title", Environment.MachineName));
        s.AppendLine(Loc.T("Report.Text.Command", CommandLine));
        s.AppendLine(Loc.T("Report.Text.Status", StatusText, ExitCode));
        s.AppendLine(Loc.T("Report.Text.Time", Started.ToString("dd.MM.yyyy HH:mm:ss"), Finished.ToString("HH:mm:ss"), Elapsed(Finished - Started)));
        if (Containers is { } c)
            s.AppendLine(Loc.T("Report.Text.Containers", c.Count, Format.Bytes(c.Sum(p => p.FileSize)), c.Count(p => p.Severity == Severity.Critical),
                               c.Count(p => p.Severity == Severity.Warning), c.Count(p => p.IsMounted)));
        if (HasOperation)
            s.AppendLine(DryRun
                ? Loc.T("Report.Text.Planned", Items.Count)
                : Loc.T("Report.Text.Results", Ok, Skipped, Failed, NotStarted, Format.Bytes(Saved)));
        s.AppendLine();
        s.AppendLine(Loc.T("Report.Text.SeeHtml"));
        return s.ToString();
    }

    // ───────────────────────────── CSV attachment ─────────────────────────────

    /// <summary>All containers, one row each (UTF-8 with BOM, ";" — opens in Excel with any locale).</summary>
    public byte[] ToCsv()
    {
        var s = new StringBuilder();
        s.AppendLine(Loc.T("Report.Csv.Header"));
        var results = Items.ToDictionary(i => i.Profile, i => i);
        foreach (var p in (Containers ?? new()).OrderByDescending(p => p.UsagePercent ?? 0))
        {
            results.TryGetValue(p, out var it);
            s.AppendLine(string.Join(';', new[]
            {
                p.DisplayUser, p.FileName, p.FilePath, p.ContainerType.ToString(), p.FileSize.ToString(),
                p.VirtualSize?.ToString() ?? "", p.UsagePercent?.ToString("0.#", CultureInfo.InvariantCulture) ?? "",
                Loc.T(p.UsageIsExact ? "Report.Csv.Yes" : "Report.Csv.No"), p.StateLabel, p.MountedBy == "—" ? "" : p.MountedBy,
                p.Growth?.ToString() ?? "", p.DaysToLimit?.ToString() ?? "", p.Reclaimable?.ToString() ?? "",
                p.AdText, p.LastWrite.ToString("yyyy-MM-dd HH:mm"), p.FlagsText,
                it == null ? "" : OutcomeText(it.Outcome) + (it.Message.Length > 0 ? ": " + it.Message : ""),
            }.Select(Csv)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(s.ToString())).ToArray();
    }

    private static string Csv(string v) =>
        v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    public static string OutcomeText(ItemOutcome o) => o switch
    {
        ItemOutcome.Ok => Loc.T("Report.Outcome.Ok"),
        ItemOutcome.Skipped => Loc.T("Report.Outcome.Skipped"),
        ItemOutcome.Failed => Loc.T("Report.Outcome.Failed"),
        _ => Loc.T("Report.Outcome.Planned"),
    };

    public static string Elapsed(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    // ───────────────────────────── HTML ─────────────────────────────

    // palette of Themes/Theme.xaml
    private const string Bg = "#0F1115", Surface = "#171A21", Surface2 = "#1E222B", Line = "#2A2F3A", Text = "#E6E8EC",
        Muted = "#8B93A3", Accent = "#4C8DFF", OkC = "#3FB950", WarnC = "#D9A520", CritC = "#F0524B", MountC = "#A371F7", Track = "#262B35";
    private const string Font = "font-family:'Segoe UI',Tahoma,Arial,sans-serif;";
    private const string Mono = "font-family:Consolas,'Courier New',monospace;";

    /// <summary>Rows of the detail tables (the CSV attachment always has everything).</summary>
    public const int MaxRows = 50;

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private string StatusColor => ExitCode switch
    {
        Headless.ExitOk => DryRun ? Accent : OkC,
        Headless.ExitCancelled or Headless.ExitAlreadyRunning => WarnC,
        _ => CritC,
    };

    private static string SevColor(Severity s) => s switch { Severity.Critical => CritC, Severity.Warning => WarnC, _ => OkC };

    private static string OutcomeColor(ItemOutcome o) => o switch
    {
        ItemOutcome.Ok => OkC, ItemOutcome.Skipped => WarnC, ItemOutcome.Failed => CritC, _ => Accent,
    };

    /// <param name="logoCid">Content-ID of the inline logo, or null.</param>
    public string ToHtml(string? logoCid)
    {
        var h = new StringBuilder();
        h.Append($"""
            <!DOCTYPE html><html lang="{Loc.Lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="dark"><meta name="supported-color-schemes" content="dark"><title>{E(Subject)}</title></head>
            <body style="margin:0;padding:0;background:{Bg};" bgcolor="{Bg}">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{Bg}" style="background:{Bg};">
            <tr><td align="center" style="padding:24px 12px;">
            <table role="presentation" width="960" cellpadding="0" cellspacing="0" border="0" style="width:960px;max-width:960px;{Font}color:{Text};font-size:14px;">
            """);

        Header(h, logoCid);
        if (HasOperation) OperationCards(h);
        if (Containers != null) ContainerCards(h);
        if (Paths.Count > 0) Storage(h);
        if (HasOperation && Items.Count > 0) Details(h);
        if (Containers != null) Attention(h);
        if (Problems.Count > 0) LogProblems(h);
        Footer(h);

        h.Append("</table></td></tr></table></body></html>");
        return h.ToString();
    }

    private void Header(StringBuilder h, string? logoCid)
    {
        var logo = logoCid != null
            ? $"""<img src="cid:{logoCid}" width="40" height="40" alt="" style="display:block;border:0;">"""
            : "";
        h.Append($"""
            <tr><td bgcolor="{Surface}" style="background:{Surface};border:1px solid {Line};border-radius:10px;padding:18px 20px;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
                <td width="52" valign="middle">{logo}</td>
                <td valign="middle" style="{Font}">
                  <div style="font-size:20px;font-weight:600;color:{Text};">FSLTool</div>
                  <div style="font-size:13px;color:{Muted};">{E(Loc.T("Report.Html.Subtitle", Environment.MachineName))}</div>
                </td>
                <td align="right" valign="middle">
                  <span style="display:inline-block;padding:6px 14px;border-radius:14px;border:1px solid {StatusColor};color:{StatusColor};font-weight:600;font-size:14px;{Font}">{E(StatusText)}</span>
                </td>
              </tr></table>
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin-top:14px;{Font}font-size:13px;">
            """);
        void Row(string k, string v) =>
            h.Append($"""<tr><td width="150" style="color:{Muted};padding:3px 0;" valign="top">{E(k)}</td><td style="color:{Text};padding:3px 0;">{v}</td></tr>""");
        Row(Loc.T("Report.Row.Command"), $"""<span style="{Mono}color:{Accent};">{E(CommandLine)}</span>""");
        Row(Loc.T("Report.Row.Selection"), E(FilterText()));
        Row(Loc.T("Report.Row.Start"), $"{Started:dd.MM.yyyy HH:mm:ss}");
        Row(Loc.T("Report.Row.Duration"), E(Loc.T("Report.Row.DurationValue", Elapsed(Finished - Started), Finished.ToString("HH:mm:ss"), ExitCode)));
        Row(Loc.T("Report.Row.Account"), E($"{Environment.UserDomainName}\\{Environment.UserName}"));
        h.Append("</table></td></tr>");
    }

    private string FilterText()
    {
        var parts = new List<string> { Loc.T("Report.Filter.Filter", Filter) };
        if (UserMask != null) parts.Add(Loc.T("Report.Filter.User", UserMask));
        if (Command == "compact" && MinGainGb is double g) parts.Add(Loc.T("Report.Filter.MinGain", g));
        if (MaxMinutes is int m) parts.Add(Loc.T("Report.Filter.Limit", m));
        if (MaxCount is int c) parts.Add(Loc.T("Report.Filter.MaxCount", c));
        if (DryRun) parts.Add(Loc.T("Report.Filter.DryRun"));
        return string.Join(" · ", parts);
    }

    private static void SectionTitle(StringBuilder h, string title, string? note = null) =>
        h.Append($"""
            <tr><td style="padding:22px 2px 8px 2px;{Font}">
              <span style="font-size:15px;font-weight:600;color:{Text};">{E(title)}</span>{(note != null ? $"""<span style="font-size:12px;color:{Muted};">&nbsp;&nbsp;{E(note)}</span>""" : "")}
            </td></tr>
            """);

    /// <summary>A row of stat cards like the ones above the table in the window.</summary>
    private static void Cards(StringBuilder h, IReadOnlyList<(string label, string value, string color, string sub)> cards)
    {
        h.Append("""<tr><td><table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>""");
        int w = 100 / cards.Count;
        for (int i = 0; i < cards.Count; i++)
        {
            var (label, value, color, sub) = cards[i];
            string pad = i < cards.Count - 1 ? "padding-right:8px;" : "";
            h.Append($"""
                <td width="{w}%" valign="top" style="{pad}">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
                    <td bgcolor="{Surface}" style="background:{Surface};border:1px solid {Line};border-radius:10px;padding:12px 14px;{Font}">
                      <div style="font-size:12px;color:{Muted};white-space:nowrap;">{E(label)}</div>
                      <div style="font-size:26px;font-weight:600;color:{color};line-height:32px;margin-top:4px;">{value}</div>
                      <div style="font-size:12px;color:{Muted};">{sub}</div>
                    </td></tr></table>
                </td>
                """);
        }
        h.Append("</tr></table></td></tr>");
    }

    private void OperationCards(StringBuilder h)
    {
        string what = Command switch
        {
            "analyze" => Loc.T("Report.Op.Analyze"), "compact" => Loc.T("Report.Op.Compact"), "clean" => Loc.T("Report.Op.Clean"), _ => Command,
        };
        SectionTitle(h, Loc.T(DryRun ? "Report.Op.PlanTitle" : "Report.Op.ResultTitle", what));
        if (DryRun)
        {
            Cards(h, new[]
            {
                (Loc.T("Report.Card.ToProcess"), Items.Count.ToString(), Accent, E(Loc.T("Report.Card.Selected", Selected))),
                (Loc.T("Report.Card.Mounted"), MountedSkipped.ToString(), MountC, E(Loc.T("Report.Card.MountedAlwaysSkipped"))),
                (Loc.T("Report.Card.Size"), E(Format.Bytes(Items.Sum(i => i.SizeBefore))), Text, E(Loc.T("Report.Card.SizeSub"))),
            });
            return;
        }
        Cards(h, new[]
        {
            (Loc.T("Report.Outcome.Ok"), Ok.ToString(), OkC, E(Loc.T("Report.Card.OkSub", Items.Count + NotStarted))),
            (Loc.T("Report.Outcome.Skipped"), Skipped.ToString(), Skipped > 0 ? WarnC : Text,
                E(MountedSkipped > 0 ? Loc.T("Report.Card.SkippedMounted", MountedSkipped) : Loc.T("Report.Card.SkippedSub"))),
            (Loc.T("Report.Card.Failed"), Failed.ToString(), Failed > 0 ? CritC : Text, E(Loc.T("Report.Card.FailedSub"))),
            (Loc.T("Report.Card.NotStarted"), NotStarted.ToString(), NotStarted > 0 ? WarnC : Text,
                E(MaxMinutes is int m ? Loc.T("Report.Filter.Limit", m) : Loc.T("Report.Card.NoLimit"))),
            (Loc.T("Report.Card.Saved"), E(Format.Bytes(Saved)), Saved > 0 ? OkC : Text, E(Loc.T("Report.Card.SavedSub", Elapsed(Finished - Started)))),
        });
    }

    private void ContainerCards(StringBuilder h)
    {
        var c = Containers!;
        long Size(IEnumerable<ProfileInfo> q) => q.Sum(p => p.FileSize);
        var compact = c.Where(p => p.WorthCompacting).ToList();
        SectionTitle(h, Loc.T("Report.Containers.Title"), HasOperation && !DryRun ? Loc.T("Report.Containers.Note") : null);
        Cards(h, new[]
        {
            (Loc.T("Report.Card.Containers"), c.Count.ToString(), Text, E(Format.Bytes(Size(c)))),
            (Loc.T("Report.Card.Overfull"), c.Count(p => p.Severity == Severity.Critical).ToString(), CritC,
                // {0} is HTML: encode the text around it, then insert the coloured count
                E(Loc.T("Report.Card.OverfullSub")).Replace("{0}", $"""<span style="color:{WarnC};">{c.Count(p => p.Severity == Severity.Warning)}</span>""")),
            (Loc.T("Report.Card.Mounted"), c.Count(p => p.IsMounted).ToString(), MountC, E(Loc.T("Report.Card.MountedSub"))),
            (Loc.T("Report.Card.Orphans"), c.Count(p => p.IsOrphan).ToString(), Text, E(Loc.T("Report.Card.OrphansSub", Format.Bytes(Size(c.Where(p => p.IsOrphan)))))),
        });
        h.Append("""<tr><td style="height:8px;font-size:0;line-height:0;">&nbsp;</td></tr>""");
        Cards(h, new[]
        {
            (Loc.T("Report.Card.Disabled"), c.Count(p => p.IsDisabled).ToString(), Text, E(Format.Bytes(Size(c.Where(p => p.IsDisabled))))),
            (Loc.T("Report.Card.Stale"), c.Count(p => p.IsStale).ToString(), Text, E(Loc.T("Report.Card.StaleSub", ProfileInfo.StaleDays))),
            (Loc.T("Report.Card.Compactable"), compact.Count.ToString(), Accent, E(Loc.T("Report.Card.CompactableSub", Format.Bytes(compact.Sum(p => p.Reclaimable ?? 0))))),
            (Loc.T("Report.Card.Eta"), c.Count(p => p.EtaSoon).ToString(), c.Any(p => p.EtaSoon) ? WarnC : Text, E(Loc.T("Report.Card.EtaSub"))),
        });
    }

    /// <summary>Usage bar as nested tables (renders in Outlook too).</summary>
    private static string Bar(double? percent, string color, int width = 120)
    {
        if (percent is not double p) return $"""<span style="color:{Muted};">—</span>""";
        int filled = (int)Math.Round(Math.Clamp(p, 0, 100) * width / 100);
        string cell(int w, string c) => w <= 0 ? "" :
            $"""<td width="{w}" height="6" bgcolor="{c}" style="background:{c};font-size:0;line-height:0;height:6px;">&nbsp;</td>""";
        return $"""<table role="presentation" width="{width}" cellpadding="0" cellspacing="0" border="0" style="border-radius:3px;"><tr>{cell(filled, color)}{cell(width - filled, Track)}</tr></table>""";
    }

    private void Storage(StringBuilder h)
    {
        SectionTitle(h, Loc.T("Report.Storage.Title"));
        TableStart(h, (Loc.T("Report.Col.Path"), null), (Loc.T("Report.Col.Usage"), "160"), (Loc.T("Report.Col.Free"), "200"), (Loc.T("Report.Col.Containers"), "180"));
        foreach (var p in Paths)
        {
            string bar = p.HasSpace
                ? $"""{Bar(p.UsedPercent, SevColor(p.Severity))}<div style="font-size:12px;color:{SevColor(p.Severity)};margin-top:3px;">{p.UsedPercent:0}%</div>"""
                : $"""<span style="color:{(p.Error != null ? CritC : Muted)};">{E(p.Error != null ? Loc.T("Report.Storage.Unavailable") : "—")}</span>""";
            Tr(h, $"""<span style="{Mono}">{E(p.Path)}</span>""", bar,
               E(p.HasSpace ? Loc.T("Report.Storage.FreeOf", Format.Bytes(p.Free!.Value), Format.Bytes(p.Total!.Value)) : p.Error ?? "—"),
               E(p.ContainersCount == 0 ? "—" : $"{p.ContainersCount} · {Format.Bytes(p.ContainersBytes)}"));
        }
        TableEnd(h);
    }

    private void Details(StringBuilder h)
    {
        // failures first, then skipped, then the rest in processing order
        var rows = Items.Select((it, i) => (it, i))
            .OrderBy(x => x.it.Outcome switch { ItemOutcome.Failed => 0, ItemOutcome.Skipped => 1, _ => 2 }).ThenBy(x => x.i)
            .Select(x => x.it).ToList();
        SectionTitle(h, Loc.T(DryRun ? "Report.Details.PlanTitle" : "Report.Details.Title"),
            rows.Count > MaxRows ? Loc.T("Report.Details.FirstOf", MaxRows, rows.Count) : $"{rows.Count}");
        if (DryRun)
            TableStart(h, (Loc.T("Report.Col.UserFile"), null), (Loc.T("Report.Col.Usage"), "150"), (Loc.T("Report.Col.SizeMax"), "170"), (Loc.T("Report.Col.Plan"), "300"));
        else
            TableStart(h, (Loc.T("Report.Col.UserFile"), null), (Loc.T("Report.Col.SizeBeforeAfter"), "190"), (Loc.T("Report.Col.Time"), "60"), (Loc.T("Report.Col.Result"), "360"));
        foreach (var it in rows.Take(MaxRows))
        {
            var p = it.Profile;
            string who = $"""<div style="color:{Text};">{E(p.DisplayUser)}</div><div style="font-size:12px;color:{Muted};">{E(p.FileName)}</div>""";
            if (DryRun)
            {
                Tr(h, who, Usage(p), E($"{p.SizeText} / {p.MaxSizeText}"), $"""<span style="color:{Muted};">{E(it.Message)}</span>""");
                continue;
            }
            long diff = it.SizeAfter - it.SizeBefore;
            string size = $"{E(Format.Bytes(it.SizeBefore))} → {E(Format.Bytes(it.SizeAfter))}" +
                          (diff != 0 ? $"""<div style="font-size:12px;color:{(diff < 0 ? OkC : Muted)};">{(diff < 0 ? "−" : "+")}{E(Format.Bytes(Math.Abs(diff)))}</div>""" : "");
            string res = $"""<span style="display:inline-block;padding:1px 8px;border-radius:9px;border:1px solid {OutcomeColor(it.Outcome)};color:{OutcomeColor(it.Outcome)};font-size:12px;">{E(OutcomeText(it.Outcome))}</span>""" +
                         $"""<div style="font-size:12px;color:{(it.Outcome == ItemOutcome.Failed ? CritC : Muted)};margin-top:3px;">{E(it.Message)}</div>""";
            Tr(h, who, size, Elapsed(it.Duration), res);
        }
        TableEnd(h);
    }

    private static string Usage(ProfileInfo p) =>
        $"""{Bar(p.UsagePercent, SevColor(p.Severity))}<div style="font-size:12px;color:{SevColor(p.Severity)};margin-top:3px;">{E(p.UsageText)}</div>""";

    private void Attention(StringBuilder h)
    {
        var list = Containers!
            .Where(p => p.Severity != Severity.Ok || p.EtaSoon || p.FsDirty == true)
            .OrderByDescending(p => p.Severity).ThenBy(p => p.DaysToLimit ?? int.MaxValue).ThenByDescending(p => p.UsagePercent ?? 0)
            .ToList();
        if (list.Count == 0) return;
        SectionTitle(h, Loc.T("Report.Attention.Title"), Loc.T("Report.Attention.Note",
            list.Count > MaxRows ? Loc.T("Report.Attention.FirstOf", MaxRows, list.Count) : $"{list.Count}", ProfileInfo.WarnPercent));
        TableStart(h, (Loc.T("Report.Col.UserFile"), null), (Loc.T("Report.Col.Usage"), "150"), (Loc.T("Report.Col.SizeMax"), "170"),
            (Loc.T("Report.Col.Growth"), "150"), (Loc.T("Report.Col.State"), "130"));
        foreach (var p in list.Take(MaxRows))
        {
            string who = $"""<div style="color:{Text};">{E(p.DisplayUser)}</div><div style="font-size:12px;color:{Muted};">{E(p.FileName)}</div>""" +
                         (p.FlagsText.Length > 0 ? $"""<div style="font-size:12px;color:{WarnC};">{E(p.FlagsText)}</div>""" : "");
            string growth = E(p.GrowthText) + (p.EtaText.Length > 0
                ? $"""<div style="font-size:12px;color:{(p.EtaSoon ? WarnC : Muted)};">{E(p.EtaText)}</div>""" : "");
            string state = $"""<span style="color:{(p.IsMounted ? MountC : Muted)};">{E(p.StateLabel)}</span>""" +
                           (p.StateHost.Length > 0 ? $"""<div style="font-size:12px;color:{Muted};">{E(p.StateHost)}</div>""" : "");
            Tr(h, who, Usage(p), E($"{p.SizeText} / {p.MaxSizeText}"), growth, state);
        }
        TableEnd(h);
        h.Append($"""<tr><td style="padding:6px 2px 0 2px;font-size:12px;color:{Muted};{Font}">{E(Loc.T("Report.Attention.Footnote"))}</td></tr>""");
    }

    private void LogProblems(StringBuilder h)
    {
        const int max = 40;
        SectionTitle(h, Loc.T("Report.Log.Title"), Problems.Count > max ? Loc.T("Report.Log.LastOf", max, Problems.Count) : $"{Problems.Count}");
        h.Append($"""<tr><td bgcolor="{Surface}" style="background:{Surface};border:1px solid {Line};border-radius:10px;padding:10px 14px;{Mono}font-size:12px;">""");
        foreach (var (t, lvl, msg) in Problems.Skip(Math.Max(0, Problems.Count - max)))
            h.Append($"""<div style="padding:2px 0;color:{Text};"><span style="color:{Muted};">{t:HH:mm:ss}</span> <span style="color:{(lvl == "ERROR" ? CritC : WarnC)};">{lvl,-5}</span> {E(msg)}</div>""");
        h.Append("</td></tr>");
    }

    private void Footer(StringBuilder h)
    {
        var ver = typeof(RunReport).Assembly.GetName().Version?.ToString(3);
        string attachments = Loc.T("Report.Footer.Attachments", Containers != null ? Loc.T("Report.Footer.Csv") : "");
        string audit = Loc.T("Report.Footer.AuditLog", $@"%ProgramData%\FSLTool\logs\fsltool-{Started:yyyyMMdd}.log");
        string generated = Loc.T("Report.Footer.Generated", ver, Finished.ToString("dd.MM.yyyy HH:mm:ss"));
        h.Append($"""
            <tr><td style="padding:22px 2px 0 2px;font-size:12px;color:{Muted};{Font}border-top:0;">
              {E(attachments)}
              {E(audit)}<br>
              {E(generated)}
            </td></tr>
            """);
    }

    private static void TableStart(StringBuilder h, params (string title, string? width)[] cols)
    {
        h.Append($"""
            <tr><td bgcolor="{Surface}" style="background:{Surface};border:1px solid {Line};border-radius:10px;padding:4px 0;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="{Font}font-size:13px;"><tr>
            """);
        foreach (var (title, width) in cols)
            h.Append($"""<td{(width != null ? $" width=\"{width}\"" : "")} style="padding:8px 14px;color:{Muted};font-size:12px;border-bottom:1px solid {Line};">{E(title)}</td>""");
        h.Append("</tr>");
    }

    private static void Tr(StringBuilder h, params string[] cells)
    {
        h.Append("<tr>");
        foreach (var c in cells)
            h.Append($"""<td valign="top" style="padding:8px 14px;color:{Text};border-bottom:1px solid {Surface2};">{c}</td>""");
        h.Append("</tr>");
    }

    private static void TableEnd(StringBuilder h) => h.Append("</table></td></tr>");
}
