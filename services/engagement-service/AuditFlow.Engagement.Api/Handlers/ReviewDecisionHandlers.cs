using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;

namespace AuditFlow.Engagement.Api.Handlers;

/// <summary>
/// Applies a reviewer's decision (recorded and audited by review-service) to the lifecycle through the one state machine:
/// Under Review -> Approved | Rejected. A decision for an engagement that is no longer Under Review, or from someone who is
/// not the assigned Reviewer, is logged and ignored. Redelivery is absorbed by the idempotent consumer pipeline.
/// </summary>
public sealed class ReviewDecisionApplier(
    IEngagementRepository repository, IOutbox outbox, TimeProvider clock, ILogger<ReviewDecisionApplier> logger)
{
    public async Task ApplyAsync(string engagementId, Trigger trigger, Guid reviewerId, EventActor? actor, IUnitOfWork uow, CancellationToken ct)
    {
        var engagement = await repository.GetRowAsync(uow, engagementId, ct)
                         ?? throw new InvalidOperationException($"{trigger} decision for unknown engagement {engagementId}; retrying.");

        var role = (await repository.GetAsync(engagementId, reviewerId, ct))?.MyRole;
        var context = new TransitionContext(role, IsAssignedReviewer: engagement.ReviewerUserId == reviewerId, HasRejectComment: true);
        var decision = EngagementStateMachine.Evaluate(engagement.StatusValue, trigger, context);
        if (!decision.Allowed)
        {
            logger.LogWarning("{Trigger} on {EngagementId} ignored: {Reason}", trigger, engagementId, decision.Reason);
            return;
        }

        var version = await repository.TransitionAsync(uow, engagementId, engagement.StatusValue, decision.To!.Value, clock.GetUtcNow(), ct);
        if (version is null) return; // someone else already moved it

        await outbox.EnqueueAsync(uow, EventEnvelope<EngagementStatusChangedPayload>.Create(
            EventTypes.EngagementStatusChanged, engagementId,
            new EngagementStatusChangedPayload(engagement.StatusValue, decision.To.Value, trigger.ToString(), RowVersion.ToToken(version)),
            actor), ct);
    }
}

public sealed class ReviewApprovedHandler(ReviewDecisionApplier applier) : IEventHandler<ReviewApprovedPayload>
{
    public Task HandleAsync(EventEnvelope<ReviewApprovedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
        => applier.ApplyAsync(envelope.EngagementId, Trigger.Approve, envelope.Payload.ReviewerId, envelope.Actor, uow, ct);
}

public sealed class ReviewRejectedHandler(ReviewDecisionApplier applier) : IEventHandler<ReviewRejectedPayload>
{
    // The comment itself lives in review-service; its presence is enforced there (400 + CHECK constraint).
    public Task HandleAsync(EventEnvelope<ReviewRejectedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
        => applier.ApplyAsync(envelope.EngagementId, Trigger.Reject, envelope.Payload.ReviewerId, envelope.Actor, uow, ct);
}
