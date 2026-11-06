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

    /// <summary>Left panel collapsed to a narrow strip (more room for the table).</summary>
    public bool SidebarCollapsed { get; set; }
}
