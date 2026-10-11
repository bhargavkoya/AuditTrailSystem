using AuditFlow.Attachment.Api.Data;
using AuditFlow.Attachment.Api.Storage;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;

namespace AuditFlow.Attachment.Api.Domain;

public sealed record StoredAttachment(AttachmentDto Dto, string StorageKey, string? ReplacedStorageKey);

/// <summary>
/// Stores a file and makes it the current attachment of its scope, inside the caller's transaction, writing AttachmentAdded
/// to the outbox. Used by uploads and by the demo seeder. The caller commits, then removes the replaced file.
/// </summary>
public sealed class AttachmentWriter(IAttachmentRepository repository, IFileStore store, IOutbox outbox, TimeProvider clock)
{
    public async Task<StoredAttachment> AddAsync(
        IUnitOfWork uow, NewAttachment attachment, Stream content, EventActor? actor, CancellationToken ct)
    {
        var storageKey = $"{attachment.EngagementId}/{Guid.NewGuid():N}";
        await store.SaveAsync(storageKey, content, ct);

        try
        {
            var id = Guid.NewGuid();
            var now = clock.GetUtcNow();
            var replaced = await repository.ReplaceCurrentAsync(uow, id, storageKey, now, attachment, ct);

            await outbox.EnqueueAsync(uow, EventEnvelope<AttachmentAddedPayload>.Create(
                EventTypes.AttachmentAdded, attachment.EngagementId,
                new AttachmentAddedPayload(attachment.Scope, attachment.TabNo, attachment.SectionKey, id, attachment.FileName), actor), ct);

            var dto = new AttachmentDto(id, attachment.EngagementId, attachment.Scope, attachment.TabNo, attachment.SectionKey,
                attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.UploadedByName, now);
            return new StoredAttachment(dto, storageKey, replaced);
        }
        catch
        {
            await store.DeleteAsync(storageKey, CancellationToken.None); // do not leave an orphan behind
            throw;
        }
    }
}
