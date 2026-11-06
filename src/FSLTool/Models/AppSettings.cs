namespace FSLTool.Models;

public sealed class AppSettings
{
    /// <summary>Folders that contain FSLogix containers (local paths or UNC).</summary>
    public List<string> ProfilePaths { get; set; } = new();

    /// <summary>Usage (%) from which a container is highlighted as "warning".</summary>
    public double WarnPercent { get; set; } = 80;

    /// <summary>Usage (%) from which a container is considered overfull.</summary>
    public double CriticalPercent { get; set; } = 90;

    /// <summary>Path to cleanup rules. Empty = %ProgramData%\FSLTool\cleanup-rules.json.</summary>
    public string? RulesFile { get; set; }

    /// <summary>Compact the VHD(X) after cleanup.</summary>
    public bool CompactAfterCleanup { get; set; } = true;

    /// <summary>Profiles not modified for this many days are flagged as stale.</summary>
    public int StaleDays { get; set; } = 90;

    /// <summary>Containers that a compaction would shrink by at least this many GB are "worth compacting".</summary>
    public double CompactMinGb { get; set; } = 1;

    public double WindowWidth { get; set; } = 1480;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    /// <summary>Email report of the headless mode (edited in settings.json; --smtp / --mail-* options override it).</summary>
    public MailSettings Mail { get; set; } = new();
}

/// <summary>"mail" section of settings.json: where the headless mode sends its report.</summary>
public sealed class MailSettings
{
    /// <summary>SMTP server; empty = reports are not sent unless --smtp is given.</summary>
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 25;
    /// <summary>STARTTLS (explicit TLS, e.g. port 587). Implicit TLS on 465 is not supported by SmtpClient.</summary>
    public bool Ssl { get; set; }
    /// <summary>Sender; empty = fsltool@&lt;computer FQDN&gt;.</summary>
    public string? From { get; set; }
    /// <summary>Recipients; empty = no report unless --mail-to is given.</summary>
    public List<string> To { get; set; } = new();
    /// <summary>SMTP login; empty = no authentication (anonymous relay).</summary>
    public string? User { get; set; }
    /// <summary>Name of the environment variable that holds the SMTP password (the password itself is never stored).</summary>
    public string? PasswordEnv { get; set; }
    /// <summary>always | problems | errors.</summary>
    public string When { get; set; } = "always";
}
