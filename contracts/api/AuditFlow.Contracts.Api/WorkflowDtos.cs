using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuditFlow.Contracts.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueSeverity
{
    Error,
    Warning,
    Information
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueScope
{
    Field,
    Section,
    Tab,
    Engagement
}

/// <summary>Result of comparing an expected and an actual value. Worst of the percentage and value levels.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VarianceIndicator
{
    Ok,
    Warning,
    Significant,
    Missing
}

/// <summary>Field kinds the generic renderer understands.</summary>
public static class FieldTypes
{
    public const string Checkbox = "checkbox";
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Documentation = "documentation";
}

/// <summary>A validation message (banner). Targets are optional and narrow with the scope.</summary>
public sealed record IssueDto(
    string Code,
    IssueSeverity Severity,
    IssueScope Scope,
    string Message,
    int? TabNo,
    string? SectionKey,
    string? FieldKey,
    bool IsBlocking);

public sealed record IssueCountsDto(int Errors, int Warnings, int Infos);

public sealed record TabSummaryDto(int TabNo, string Title, IssueCountsDto IssueCounts);

/// <summary><c>Ready</c> is false while workflow-service is still provisioning a freshly created engagement (UI polls).</summary>
public sealed record TabsDto(bool Ready, IReadOnlyList<TabSummaryDto> Tabs);

/// <summary>
/// One field exactly as the UI should render it. <c>Value</c> is a bool, string (dates as yyyy-MM-dd), number or null.
/// Label, type, required and editable are decided by the backend.
/// </summary>
public sealed record FieldViewDto(
    string Key,
    string Type,
    string Label,
    object? Value,
    bool Required,
    bool Editable,
    IReadOnlyList<IssueDto> Issues);

/// <summary>Attachment affordances for a section. Omitted entirely for roles that cannot see attachments.</summary>
public sealed record SectionAttachmentDto(bool Allowed, bool Required, bool CanUpload, bool HasCurrent);

public sealed record SectionViewDto(
    string SectionKey,
    string Title,
    bool Editable,
    string Version,
    IReadOnlyList<FieldViewDto> Fields,
    SectionAttachmentDto? Attachment,
    IReadOnlyList<IssueDto> Issues);

public sealed record ReconciliationRowDto(
    string ComparisonKey,
    string Label,
    decimal? Expected,
    decimal? Actual,
    decimal? ValueVariance,
    decimal? PercentVariance,
    VarianceIndicator Indicator,
    bool IsSignificant,
    bool IsMissing);

/// <summary>A whole tab: visible sections only, with tab-scope issues and (tab 5) computed reconciliation rows.</summary>
public sealed record TabViewDto(
    int TabNo,
    string Title,
    bool Editable,
    IReadOnlyList<SectionViewDto> Sections,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<ReconciliationRowDto>? Reconciliation);

public sealed record ValidationSummaryDto(
    IssueCountsDto Counts,
    bool ReadyToSubmit,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<ReconciliationRowDto> Reconciliation);

/// <summary>PATCH /engagements/{id}/sections/{key}. The section version travels in If-Match.</summary>
public sealed record PatchSectionRequest(IReadOnlyDictionary<string, JsonElement> Fields);

public sealed record PatchTabSectionRequest(string SectionKey, string Version, IReadOnlyDictionary<string, JsonElement> Fields);

/// <summary>PATCH /engagements/{id}/tabs/{tabNo}: an atomic batch, each section carrying its own version.</summary>
public sealed record PatchTabRequest(IReadOnlyList<PatchTabSectionRequest> Sections);

/// <summary>A section that became visible or hidden because Tab 1 changed.</summary>
public sealed record VisibilityChangeDto(string SectionKey, bool Visible);

public sealed record SaveSectionResultDto(
    SectionViewDto Section,
    ValidationSummaryDto Summary,
    IReadOnlyList<VisibilityChangeDto> VisibilityChanges);

public sealed record SaveTabResultDto(
    TabViewDto Tab,
    ValidationSummaryDto Summary,
    IReadOnlyList<VisibilityChangeDto> VisibilityChanges);

/// <summary>Internal: what engagement-service needs to decide a submit (consumed in Phase 3).</summary>
public sealed record SubmissionReadinessDto(bool ReadyToSubmit, IReadOnlyList<IssueDto> Errors);

/// <summary>Internal: what attachment-service needs to accept a section-scoped upload.</summary>
public sealed record SectionPolicyDto(bool Exists, bool Visible, int TabNo, bool AttachmentAllowed);

/// <summary>PATCH /engagements/{id} (Draft only). Null means unchanged.</summary>
public sealed record UpdateEngagementRequest(string? Name, string? PeriodType, string? Region, Guid? ReviewerUserId);
