using AuditFlow.BuildingBlocks.Data;
using AuditFlow.Contracts.Api;
using Dapper;

namespace AuditFlow.Attachment.Api.Data;

public sealed record NewAttachment(
    string EngagementId, AttachmentScope Scope, int? TabNo, string? SectionKey,
    string FileName, string ContentType, long SizeBytes, Guid UploadedByUserId, string UploadedByName);

public interface IAttachmentRepository
{
    /// <summary>
    /// Makes the new row the current one for its scope. Serialized per scope with an application lock so two concurrent
    /// uploads never trip the unique index. Returns the storage key of the file that was replaced, if any.
    /// </summary>
    Task<string?> ReplaceCurrentAsync(IUnitOfWork uow, Guid attachmentId, string storageKey, DateTimeOffset now, NewAttachment attachment, CancellationToken ct);

    Task<IReadOnlyList<AttachmentDto>> ListCurrentAsync(string engagementId, CancellationToken ct);

    Task<(AttachmentDto Dto, string StorageKey)?> GetCurrentAsync(string engagementId, Guid attachmentId, CancellationToken ct);
}

public sealed class AttachmentRepository(IDbConnectionFactory connections) : IAttachmentRepository
{
    private const string Columns = """
        AttachmentId, EngagementId, ScopeType, CAST(TabNo AS INT) AS TabNo, SectionKey, FileName, ContentType, SizeBytes,
        StorageKey, UploadedByName, UploadedAt
        """;

    public async Task<string?> ReplaceCurrentAsync(
        IUnitOfWork uow, Guid attachmentId, string storageKey, DateTimeOffset now, NewAttachment a, CancellationToken ct)
    {
        var scopeKey = a.Scope switch
        {
            AttachmentScope.Engagement => "engagement",
            AttachmentScope.Tab => $"tab:{a.TabNo}",
            _ => $"section:{a.SectionKey}"
        };

        var lockResult = await uow.QuerySingleOrDefaultAsync<int>("""
            DECLARE @r INT;
            EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            SELECT @r;
            """, new { resource = $"att:{a.EngagementId}:{scopeKey}" }, ct);
        if (lockResult < 0) throw new InvalidOperationException("Could not lock the attachment scope; try again.");

        var replaced = (await uow.QueryAsync<string>("""
            UPDATE dbo.Attachments SET IsCurrent = 0
            OUTPUT deleted.StorageKey
            WHERE EngagementId = @EngagementId AND ScopeKey = @scopeKey AND IsCurrent = 1
            """, new { a.EngagementId, scopeKey }, ct)).FirstOrDefault();

        await uow.ExecuteAsync("""
            INSERT INTO dbo.Attachments
                (AttachmentId, EngagementId, ScopeType, TabNo, SectionKey, FileName, ContentType, SizeBytes, StorageKey,
                 UploadedByUserId, UploadedByName, UploadedAt, IsCurrent)
            VALUES
                (@attachmentId, @EngagementId, @ScopeType, @TabNo, @SectionKey, @FileName, @ContentType, @SizeBytes, @storageKey,
                 @UploadedByUserId, @UploadedByName, @now, 1)
            """, new
        {
            attachmentId, a.EngagementId, ScopeType = a.Scope.ToString(), a.TabNo, a.SectionKey, a.FileName, a.ContentType,
            a.SizeBytes, storageKey, a.UploadedByUserId, a.UploadedByName, now
        }, ct);

        return replaced;
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListCurrentAsync(string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM dbo.Attachments WHERE EngagementId = @engagementId AND IsCurrent = 1
            ORDER BY CASE ScopeType WHEN N'Engagement' THEN 0 WHEN N'Tab' THEN 1 ELSE 2 END, TabNo, SectionKey
            """, new { engagementId }, cancellationToken: ct));
        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<(AttachmentDto Dto, string StorageKey)?> GetCurrentAsync(string engagementId, Guid attachmentId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition($"""
            SELECT {Columns} FROM dbo.Attachments WHERE EngagementId = @engagementId AND AttachmentId = @attachmentId AND IsCurrent = 1
            """, new { engagementId, attachmentId }, cancellationToken: ct));
        return row is null ? null : (row.ToDto(), row.StorageKey);
    }

    private sealed class Row
    {
        public Guid AttachmentId { get; init; }
        public string EngagementId { get; init; } = "";
        public string ScopeType { get; init; } = "";
        public int? TabNo { get; init; }
        public string? SectionKey { get; init; }
        public string FileName { get; init; } = "";
        public string ContentType { get; init; } = "";
        public long SizeBytes { get; init; }
        public string StorageKey { get; init; } = "";
        public string UploadedByName { get; init; } = "";
        public DateTimeOffset UploadedAt { get; init; }

        public AttachmentDto ToDto() => new(
            AttachmentId, EngagementId, Enum.Parse<AttachmentScope>(ScopeType), TabNo, SectionKey, FileName, ContentType,
            SizeBytes, UploadedByName, UploadedAt);
    }
}
