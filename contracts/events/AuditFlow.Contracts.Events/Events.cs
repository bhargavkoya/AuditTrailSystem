using AuditFlow.Contracts.Api;

namespace AuditFlow.Contracts.Events;

/// <summary>Event type names (the Service Bus message Subject and the envelope EventType).</summary>
public static class EventTypes
{
    public const string EngagementCreated = nameof(EngagementCreated);
    public const string EngagementUpdated = nameof(EngagementUpdated);
    public const string ConfigurationChanged = nameof(ConfigurationChanged);
    public const string SectionUpdated = nameof(SectionUpdated);
    public const string AttachmentAdded = nameof(AttachmentAdded);
    public const string ValidationSummaryChanged = nameof(ValidationSummaryChanged);
    public const string EngagementSubmitted = nameof(EngagementSubmitted);
    public const string ReviewerCommentAdded = nameof(ReviewerCommentAdded);
    public const string ReviewApproved = nameof(ReviewApproved);
    public const string ReviewRejected = nameof(ReviewRejected);
    public const string EngagementStatusChanged = nameof(EngagementStatusChanged);

    public static readonly IReadOnlyList<string> All =
    [
        EngagementCreated, EngagementUpdated, ConfigurationChanged, SectionUpdated, AttachmentAdded,
        ValidationSummaryChanged, EngagementSubmitted, ReviewerCommentAdded, ReviewApproved, ReviewRejected,
        EngagementStatusChanged
    ];
}

public sealed record ParticipantInfo(Guid UserId, EngagementRole Role, string DisplayName);

public sealed record ConfigurationInfo(IReadOnlyList<string> ConfigTypes, IReadOnlyDictionary<string, bool> Options);

/// <summary>
/// EngagementCreated v1. Carries status/seeded so consumers can provision seeded engagements in their current state.
/// </summary>
public sealed record EngagementCreatedPayload(
    string Name,
    string PeriodType,
    string Region,
    EngagementStatus Status,
    bool Seeded,
    Guid OwnerUserId,
    Guid ReviewerUserId,
    IReadOnlyList<ParticipantInfo> Participants,
    ConfigurationInfo Configuration);

/// <summary>
/// EngagementUpdated v1 (published from Phase 2). Carries the new values of the fields workflow uses as rule inputs,
/// so workflow-service never calls back; null means unchanged.
/// </summary>
public sealed record EngagementUpdatedPayload(
    IReadOnlyList<string> ChangedFields, string Version, string? PeriodType = null, string? Region = null);

/// <summary>EngagementStatusChanged v1 (first published in Phase 2 by the work-started / rework-started transitions).</summary>
public sealed record EngagementStatusChangedPayload(EngagementStatus From, EngagementStatus To, string Reason, string Version);

public sealed record AffectedSection(string SectionKey, bool Visible);

/// <summary>ConfigurationChanged v1: Tab 1 changed; lists the sections whose visibility flipped. No values.</summary>
public sealed record ConfigurationChangedPayload(
    IReadOnlyList<string> ChangedKeys, IReadOnlyList<AffectedSection> AffectedSections, string Version);

/// <summary>SectionUpdated v1: names of changed fields and the new section version. Never values.</summary>
public sealed record SectionUpdatedPayload(int TabNo, string SectionKey, IReadOnlyList<string> ChangedFields, string Version);

/// <summary>ValidationSummaryChanged v1. <c>Version</c> is the summary hash.</summary>
public sealed record ValidationSummaryChangedPayload(int Errors, int Warnings, int Infos, bool ReadyToSubmit, string Version);

/// <summary>AttachmentAdded v1.</summary>
public sealed record AttachmentAddedPayload(AttachmentScope Scope, int? TabNo, string? SectionKey, Guid AttachmentId, string FileName);
