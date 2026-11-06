using System.Text.Json.Serialization;

namespace FSLTool.Models;

public enum CleanupAction
{
    /// <summary>Delete everything inside the folder, keep the folder itself.</summary>
    DeleteContents,
    /// <summary>Delete the folder entirely.</summary>
    DeleteFolder,
    /// <summary>Delete only files that match <see cref="CleanupRule.Include"/> directly inside the folder.</summary>
    DeleteFiles,
}

public sealed class CleanupRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Profile, ODFC or Any.</summary>
    public List<string> Containers { get; set; } = new() { "Any" };

    public List<string> Paths { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CleanupAction Action { get; set; } = CleanupAction.DeleteContents;

    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public int OlderThanDays { get; set; }

    public bool AppliesTo(ContainerType type) =>
        Containers.Count == 0 ||
        Containers.Any(c => c.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                            c.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase));
}

public sealed class CleanupRuleSet
{
    [JsonPropertyName("_help")]
    public List<string>? Help { get; set; }

    public List<string> GlobalExclude { get; set; } = new();
    public List<CleanupRule> Rules { get; set; } = new();
}
