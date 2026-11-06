using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.NetworkInformation;
using System.Text;
using FSLTool.Models;

namespace FSLTool.Services;

/// <summary>Resolved mail options of one headless run: settings.json "mail" section overridden by --smtp / --mail-* options.</summary>
public sealed class MailConfig
{
    public string Host = "";
    public int Port = 25;
    public bool Ssl;
    public string From = "";
    public List<string> To = new();
    public string? User;
    public string? Password;
    /// <summary>always | problems | errors.</summary>
    public string When = "always";

    public static readonly string[] WhenValues = { "always", "problems", "errors" };

    public bool ShouldSend(RunReport r) => When switch
    {
        "errors" => r.ExitCode != Headless.ExitOk,
        "problems" => r.HasProblems,
        _ => true,
    };

    /// <summary>Default sender: fsltool@&lt;computer FQDN&gt;.</summary>
    public static string DefaultFrom()
    {
        string domain = "";
        try { domain = IPGlobalProperties.GetIPGlobalProperties().DomainName; } catch { }
        return $"fsltool@{Environment.MachineName.ToLowerInvariant()}" + (domain.Length > 0 ? "." + domain : ".local");
    }

    /// <summary>Splits "a@x; b@x, c@x" into addresses.</summary>
    public static List<string> SplitAddresses(string? s) =>
        (s ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Sends the headless report (HTML + plain text alternative, inline logo, .log and .csv attachments) via SMTP.</summary>
public static class ReportMailer
{
    private const string LogoCid = "fsltool-logo";

    public static void Send(MailConfig c, RunReport r)
    {
        using var msg = new MailMessage
        {
            From = new MailAddress(c.From, "FSLTool " + Environment.MachineName),
            Subject = r.Subject,
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        foreach (var a in c.To) msg.To.Add(a);
        if (r.ExitCode is not (Headless.ExitOk)) msg.Priority = MailPriority.High;

        var logo = LoadLogo();
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(r.ToText(), Encoding.UTF8, MediaTypeNames.Text.Plain));
        var html = AlternateView.CreateAlternateViewFromString(r.ToHtml(logo != null ? LogoCid : null), Encoding.UTF8, MediaTypeNames.Text.Html);
        html.TransferEncoding = TransferEncoding.Base64;   // long lines of inline CSS must not be broken by quoted-printable
        if (logo != null)
            html.LinkedResources.Add(new LinkedResource(new MemoryStream(logo), "image/png") { ContentId = LogoCid, TransferEncoding = TransferEncoding.Base64 });
        msg.AlternateViews.Add(html);

        string stamp = $"{r.Started:yyyyMMdd-HHmm}";
        msg.Attachments.Add(Attach(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(r.Output.ToString())).ToArray(),
            $"FSLTool-{r.Command}-{stamp}.log", "text/plain"));
        if (r.Containers != null)
            msg.Attachments.Add(Attach(r.ToCsv(), $"FSLTool-containers-{stamp}.csv", "text/csv"));

        using var smtp = new SmtpClient(c.Host, c.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = c.Ssl,
            Timeout = 60_000,
        };
        // UseDefaultCredentials=false resets Credentials, so it must be set first
        smtp.UseDefaultCredentials = false;
        if (!string.IsNullOrEmpty(c.User)) smtp.Credentials = new NetworkCredential(c.User, c.Password);
        smtp.Send(msg);
    }

    private static Attachment Attach(byte[] data, string name, string mime)
    {
        var a = new Attachment(new MemoryStream(data), name, mime);
        if (a.ContentType != null) a.ContentType.CharSet = "utf-8";
        return a;
    }

    private static byte[]? LoadLogo()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/FSLTool;component/Assets/logo.png"));
            if (res == null) return null;
            using var s = res.Stream;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
