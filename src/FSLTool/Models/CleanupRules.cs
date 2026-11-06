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

    /// <summary>Optional translated names: "names": { "en": "…" }. <see cref="Name"/> is used when there is none for the current language.</summary>
    public Dictionary<string, string>? Names { get; set; }

    /// <summary>Name in the current UI language (falls back to <see cref="Name"/>).</summary>
    [JsonIgnore]
    public string DisplayName => Names != null && Names.TryGetValue(Loc.Lang, out var n) && !string.IsNullOrWhiteSpace(n) ? n : Name;

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

/// <summary>
/// Rule name → display name in the UI language for names stored as data (analysis cache: RuleEstimate.RuleName).
/// Filled from the last loaded rule set; unknown names are shown as stored.
/// </summary>
public static class RuleNames
{
    private static volatile Dictionary<string, CleanupRule> _byName = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(CleanupRuleSet set)
    {
        var d = new Dictionary<string, CleanupRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in set.Rules) d.TryAdd(r.Name, r);
        _byName = d;
    }

    public static string Display(string name) => _byName.TryGetValue(name, out var r) ? r.DisplayName : name;
}
