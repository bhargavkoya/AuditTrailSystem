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
