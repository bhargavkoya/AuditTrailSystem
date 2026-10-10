namespace AuditFlow.Contracts.Api;

/// <summary>Answer to "what may this user do on this engagement right now?" (internal, engagement-service is the authority).</summary>
public sealed record EngagementAccessDto(
    string EngagementId,
    EngagementRole Role,
    EngagementStatus Status,
    string Version,
    IReadOnlyList<string> Capabilities);

public sealed record CreationOptionDto(string Key, string Label, string Type);

/// <summary>Values the create-engagement form may offer. Served by the backend so the client hardcodes nothing.</summary>
public sealed record CreationOptionsDto(
    IReadOnlyList<string> PeriodTypes,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> ConfigTypes,
    IReadOnlyList<CreationOptionDto> Options);

/// <summary>Minimal user projection shared between auth-service and its callers.</summary>
public sealed record UserSummaryDto(Guid UserId, string DisplayName, EngagementRole HomeRole);

public sealed record UserLookupRequest(IReadOnlyList<Guid> UserIds);

public sealed record LoginRequest(string Email, string Password);

public sealed record CurrentUserDto(Guid UserId, string DisplayName, string Email, EngagementRole HomeRole);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, CurrentUserDto User);

/// <summary>Global capabilities only. Per-engagement permissions come from engagement-service.</summary>
public sealed record PermissionsDto(bool CanCreateEngagement);

public sealed record ConfigurationRequest(IReadOnlyList<string> ConfigTypes, IReadOnlyDictionary<string, bool>? Options);

public sealed record CreateEngagementRequest(
    string Name,
    string PeriodType,
    string Region,
    Guid ReviewerUserId,
    IReadOnlyList<Guid>? ParticipantUserIds,
    ConfigurationRequest Configuration);

public sealed record PersonDto(Guid UserId, string DisplayName);

public sealed record ParticipantDto(Guid UserId, string DisplayName, EngagementRole Role);

/// <summary>Row of the dashboard list.</summary>
public sealed record EngagementSummaryDto(
    string EngagementId,
    string Name,
    string Region,
    string PeriodType,
    PersonDto Reviewer,
    EngagementStatus Status,
    DateTimeOffset LastUpdatedAt,
    EngagementRole MyRole,
    IReadOnlyList<string> AllowedActions);

public sealed record EngagementListDto(
    IReadOnlyList<EngagementSummaryDto> Items,
    IReadOnlyDictionary<string, int> Counts,
    int Total,
    int Page,
    int PageSize);

/// <summary>Engagement header. The client renders myRole/allowedActions/capabilities and decides nothing itself.</summary>
public sealed record EngagementDetailDto(
    string EngagementId,
    string Name,
    string Region,
    string PeriodType,
    EngagementStatus Status,
    PersonDto Owner,
    PersonDto Reviewer,
    EngagementRole MyRole,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string> Capabilities,
    string Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt);
