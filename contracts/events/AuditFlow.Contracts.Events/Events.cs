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

/// <summary>EngagementUpdated v1 (declared in Phase 1, first published in Phase 2).</summary>
public sealed record EngagementUpdatedPayload(IReadOnlyList<string> ChangedFields, string Version);

/// <summary>EngagementStatusChanged v1 (declared in Phase 1, first published in Phase 3).</summary>
public sealed record EngagementStatusChangedPayload(EngagementStatus From, EngagementStatus To, string Reason, string Version);
