using System.Text;
using AuditFlow.Attachment.Api.Data;
using AuditFlow.Attachment.Api.Domain;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;

namespace AuditFlow.Attachment.Api.Handlers;

/// <summary>
/// Demo data only: for engagements created by the seeder in a late lifecycle status, attach the sample files that their
/// completed workflow expects (source extract, legal opinion, approval pack). Event-driven like everything else, so the
/// workflow learns about them through the same AttachmentAdded events as real uploads.
/// </summary>
public sealed class SeedAttachmentsHandler(AttachmentWriter writer, ILogger<SeedAttachmentsHandler> logger) : IEventHandler<EngagementCreatedPayload>
{
    public async Task HandleAsync(EventEnvelope<EngagementCreatedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (!payload.Seeded) return;
        if (payload.Status is not (EngagementStatus.UnderReview or EngagementStatus.Approved or EngagementStatus.Rejected)) return;

        var id = envelope.EngagementId;
        var owner = payload.Participants.FirstOrDefault(p => p.UserId == payload.OwnerUserId)?.DisplayName ?? "Auditor";
        var files = new List<(AttachmentScope Scope, int? Tab, string? Section, string Name, string Text)>
        {
            (AttachmentScope.Section, null, "section-2-1", $"source-extract-{id}.txt", "Sample source data extract")
        };
        if (payload.Configuration.Options.TryGetValue("requiresLegalReview", out var legal) && legal)
            files.Add((AttachmentScope.Section, null, "section-3-3", $"legal-opinion-{id}.txt", "Sample legal opinion"));
        if (payload.Status == EngagementStatus.Approved)
            files.Add((AttachmentScope.Engagement, null, null, $"approval-pack-{id}.txt", "Sample approval pack"));

        foreach (var (scope, tab, section, name, text) in files)
        {
            var bytes = Encoding.UTF8.GetBytes($"{text} for {id}. Generated for the demo data set.");
            await writer.AddAsync(uow,
                new NewAttachment(id, scope, tab, section, name, "text/plain", bytes.Length, payload.OwnerUserId, owner),
                new MemoryStream(bytes), envelope.Actor, ct);
        }

        logger.LogInformation("Seeded {Count} sample attachments for {EngagementId}", files.Count, id);
    }
}
