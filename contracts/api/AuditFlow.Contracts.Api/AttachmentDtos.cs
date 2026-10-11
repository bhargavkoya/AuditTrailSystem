using System.Text.Json.Serialization;

namespace AuditFlow.Contracts.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttachmentScope
{
    Engagement,
    Tab,
    Section
}

/// <summary>Current (latest) attachment for a scope. There is no version history.</summary>
public sealed record AttachmentDto(
    Guid AttachmentId,
    string EngagementId,
    AttachmentScope Scope,
    int? TabNo,
    string? SectionKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string UploadedByName,
    DateTimeOffset UploadedAt);
