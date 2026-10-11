using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>
/// Versioned rule set (Open Decision 3): the section catalogue, field definitions and rules for every engagement.
/// Stored as JSON in RuleSets; evaluated by pure code. Nothing in it is known to the client.
/// </summary>
public sealed record RuleSet(
    int Version,
    IReadOnlyList<TabDefinition> Tabs,
    IReadOnlyList<SectionDefinition> Sections,
    IReadOnlyList<RuleDefinition> Rules,
    ReconciliationDefinition Reconciliation)
{
    public SectionDefinition? FindSection(string key) => Sections.FirstOrDefault(s => s.Key == key);
}

public sealed record TabDefinition(int TabNo, string Title);

public sealed record SectionDefinition(
    string Key,
    int Tab,
    string Title,
    int Order,
    Condition? VisibleWhen,
    AttachmentDefinition? Attachment,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<RuleDefinition>? Rules = null)
{
    public FieldDefinition? FindField(string key) => Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>Sections without an "attachment" block accept an optional attachment.</summary>
    public AttachmentDefinition EffectiveAttachment => Attachment ?? new AttachmentDefinition();

    public IReadOnlyList<RuleDefinition> SectionRules => Rules ?? [];
}

/// <param name="Source">How provisioning fills the field from EngagementCreated: "config:&lt;config type&gt;" or "option:&lt;option key&gt;".</param>
/// <param name="Fact">Exposes the field's value to rules under this name (e.g. "cfg.derivatives"). Only for always-visible sections.</param>
public sealed record FieldDefinition(
    string Key,
    string Type,
    string Label,
    bool Required = false,
    Condition? RequiredWhen = null,
    string? Fact = null,
    int? MaxLength = null,
    string? Source = null);

public sealed record AttachmentDefinition(bool Allowed = true, bool Required = false, Condition? RequiredWhen = null);

/// <summary>Raises an issue when <see cref="When"/> holds. Inside a section, Field targets one of its fields.</summary>
public sealed record RuleDefinition(
    string Id,
    Condition When,
    IssueSeverity Severity,
    string Message,
    IssueScope Scope = IssueScope.Section,
    string? Field = null,
    int? Tab = null);

/// <summary>
/// One node of the condition tree. Ops: equals, in, isTrue, isEmpty, gt, gte, lt, lte, lengthLt, not, all, any.
/// Comparisons take a literal (<see cref="Value"/>) or another fact (<see cref="Other"/>).
/// </summary>
public sealed record Condition(
    string Op,
    string? Fact = null,
    JsonElement? Value = null,
    string? Other = null,
    IReadOnlyList<JsonElement>? Values = null,
    IReadOnlyList<Condition>? Conditions = null,
    [property: JsonPropertyName("condition")] Condition? Inner = null);

public sealed record ReconciliationDefinition(
    Condition? EnabledWhen,
    string Section,
    IReadOnlyList<ReconciliationPair> Pairs,
    ReconciliationThresholds Thresholds);

/// <summary>Expected vs actual taken from two numeric fields of the same section.</summary>
public sealed record ReconciliationPair(string Key, string Label, string Section, string Expected, string Actual);

public sealed record ThresholdLevels(decimal Warning, decimal Significant);

/// <param name="Percent">Percentage points (1 = 1%).</param>
/// <param name="Value">Absolute amounts.</param>
public sealed record ReconciliationThresholds(ThresholdLevels Percent, ThresholdLevels Value);

public static class RuleSetJson
{
    public const string EmbeddedV1 = "AuditFlow.Workflow.Api.RuleSets.ruleset-v1.json";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static RuleSet Parse(string json)
        => JsonSerializer.Deserialize<RuleSet>(json, Options) ?? throw new InvalidOperationException("Rule set is empty.");

    public static string LoadEmbeddedJson(string resourceName = EmbeddedV1)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded rule set '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static RuleSet LoadEmbedded() => Parse(LoadEmbeddedJson());
}
