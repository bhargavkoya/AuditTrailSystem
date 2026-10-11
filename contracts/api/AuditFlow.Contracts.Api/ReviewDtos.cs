namespace AuditFlow.Contracts.Api;

/// <summary>POST /reviews/{engagementId}/comments. Tab-level only (1-5); flat and append-only.</summary>
public sealed record AddCommentRequest(int TabNo, string Body);

/// <summary>POST /reviews/{engagementId}/approve (comment optional) and /reject (comment mandatory, enforced by the backend).</summary>
public sealed record DecisionRequest(string? Comment);

public sealed record TabCommentDto(
    Guid CommentId,
    int TabNo,
    int Cycle,
    Guid AuthorUserId,
    string AuthorName,
    string Body,
    DateTimeOffset CreatedAt);

/// <summary><c>Decision</c> is "Approved" or "Rejected".</summary>
public sealed record ReviewDecisionDto(
    Guid DecisionId,
    int Cycle,
    string Decision,
    Guid ReviewerUserId,
    string ReviewerName,
    string? Comment,
    DateTimeOffset DecidedAt);

/// <summary>Body of an approve / reject / comment response. The engagement status follows from the event (eventually).</summary>
public sealed record DecisionResultDto(Guid DecisionId, int Cycle, string Decision);

/// <summary>One tab of the review screen. <c>Available</c> is false when workflow-service could not serve it (degraded, not failed).</summary>
public sealed record ReviewTabDto(int TabNo, string Title, bool Available, TabViewDto? Content);

/// <summary>
/// Everything the distinct review screen renders. The backend decides canComment / canDecide; the client only renders them.
/// <c>Cycle</c> is 0 until the engagement has been submitted at least once.
/// </summary>
public sealed record ReviewViewDto(
    EngagementDetailDto Engagement,
    int Cycle,
    bool CanComment,
    bool CanDecide,
    IReadOnlyList<ReviewTabDto> Tabs,
    ValidationSummaryDto? Validation,
    IReadOnlyList<TabCommentDto> Comments,
    IReadOnlyList<ReviewDecisionDto> History);
