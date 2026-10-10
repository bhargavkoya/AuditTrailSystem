using System.Text.Json.Serialization;

namespace AuditFlow.Contracts.Api;

/// <summary>Engagement lifecycle. Serialized by name (e.g. "UnderReview").</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EngagementStatus
{
    Draft,
    InProgress,
    UnderReview,
    Approved,
    Rejected
}

/// <summary>
/// Per-engagement role (EngagementParticipant) and also a user's home role (JWT "role" claim).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EngagementRole
{
    Auditor,
    CoAuditor,
    Reviewer,
    Viewer
}

/// <summary>Capability names returned by the engagement-service access check. The matrix lives in engagement-service only.</summary>
public static class Capabilities
{
    public const string View = "engagement.view";
    public const string Edit = "content.edit";
    public const string Attach = "attachment.upload";
    public const string AttachView = "attachment.view";
    public const string Comment = "comment.add";
    public const string CommentView = "comment.view";
    public const string Submit = "engagement.submit";
    public const string Review = "review.decide";
}

/// <summary>Quick actions the dashboard may render. Backend decides which apply.</summary>
public static class EngagementActions
{
    public const string Open = "open";
    public const string Edit = "edit";
    public const string Submit = "submit";
    public const string Review = "review";
}
