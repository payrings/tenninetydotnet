using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

public sealed class Plan
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = TenNinety.SchemaVersion;

    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("global_context")]
    public GlobalContext GlobalContext { get; set; } = new();

    /// <summary>Blueprint v3.2 Enterprise: the Architect's structural analysis of the spec.</summary>
    [JsonPropertyName("architecture_map")]
    public ArchitectureMap? ArchitectureMap { get; set; }

    [JsonPropertyName("work_packages")]
    public List<WorkPackage> WorkPackages { get; set; } = new();
}

public sealed class GlobalContext
{
    [JsonPropertyName("tech_stack")]
    public string TechStack { get; set; } = "";

    [JsonPropertyName("coding_standards")]
    public List<string> CodingStandards { get; set; } = new();

    [JsonPropertyName("assumptions")]
    public List<string> Assumptions { get; set; } = new();

    /// <summary>Blueprint v3.2 Enterprise: intended project layout, e.g. {"/src": [...], "/tests": [...]}.</summary>
    [JsonPropertyName("directory_structure")]
    public Dictionary<string, List<string>>? DirectoryStructure { get; set; }
}

public sealed class ArchitectureMap
{
    [JsonPropertyName("bounded_contexts")]
    public List<string> BoundedContexts { get; set; } = new();

    [JsonPropertyName("core_entities")]
    public List<string> CoreEntities { get; set; } = new();

    [JsonPropertyName("key_dependencies")]
    public List<string> KeyDependencies { get; set; } = new();
}

public sealed class WorkPackage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("layer")]
    public string Layer { get; set; } = "";

    /// <summary>Blueprint v3.2 Enterprise: bounded context / module this package belongs to.</summary>
    [JsonPropertyName("module")]
    public string Module { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = new();

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "";

    [JsonPropertyName("directives")]
    public List<string> Directives { get; set; } = new();

    [JsonPropertyName("acceptance_criteria")]
    public List<string> AcceptanceCriteria { get; set; } = new();

    /// <summary>
    /// Blueprint v3.2 Enterprise: free-form notes. Carries the AMBIGUOUS/CONFLICT markers of the
    /// ambiguity protocol (see <see cref="Validation.WpMarkers"/>).
    /// </summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = TenNinety.WpStatus.Pending;

    [JsonIgnore]
    public bool IsTerminal =>
        Status is TenNinety.WpStatus.Done or TenNinety.WpStatus.Blocked or TenNinety.WpStatus.Cancelled;
}
