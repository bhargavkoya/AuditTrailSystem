using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Review.Api.Data;

namespace AuditFlow.Review.Api.Handlers;

/// <summary>Opens a review cycle every time an engagement is submitted (idempotent per engagement and cycle).</summary>
public sealed class EngagementSubmittedHandler(IReviewRepository repository) : IEventHandler<EngagementSubmittedPayload>
{
    public Task HandleAsync(EventEnvelope<EngagementSubmittedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
        => repository.EnsureCycleAsync(
            uow, envelope.EngagementId, envelope.Payload.Cycle, envelope.Payload.SubmittedBy, envelope.Payload.SubmittedByName, envelope.OccurredAt, ct);
}

/// <summary>
/// Demo data only: engagements the seeder created in a review-related status get a cycle 1 with reviewer comments and, for
/// Approved / Rejected, the decision, so the review screen and history have content from the first start.
/// </summary>
public sealed class SeedReviewHandler(IReviewRepository repository, ILogger<SeedReviewHandler> logger) : IEventHandler<EngagementCreatedPayload>
{
    public async Task HandleAsync(EventEnvelope<EngagementCreatedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
    {
        var p = envelope.Payload;
        if (!p.Seeded || p.Status is not (EngagementStatus.UnderReview or EngagementStatus.Approved or EngagementStatus.Rejected)) return;

        var id = envelope.EngagementId;
        var owner = p.Participants.FirstOrDefault(x => x.UserId == p.OwnerUserId);
        var reviewerName = p.Participants.FirstOrDefault(x => x.UserId == p.ReviewerUserId)?.DisplayName ?? "Reviewer";
        var at = envelope.OccurredAt;

        await repository.EnsureCycleAsync(uow, id, 1, p.OwnerUserId, owner?.DisplayName, at, ct);

        async Task CommentAsync(int tab, string body)
            => await repository.InsertCommentAsync(uow, new NewComment(Guid.NewGuid(), id, 1, tab, p.ReviewerUserId, reviewerName, body, at), ct);

        switch (p.Status)
        {
            case EngagementStatus.UnderReview:
                await CommentAsync(2, "Please double-check the opening balances against the source extract.");
                await CommentAsync(3, "Procedure coverage looks complete so far.");
                break;
            case EngagementStatus.Approved:
                await CommentAsync(5, "Reconciliation variances are explained and within tolerance.");
                await repository.TryInsertDecisionAsync(uow, new NewDecision(
                    Guid.NewGuid(), id, 1, "Approved", p.ReviewerUserId, reviewerName, "Approved after review.", at), ct);
                break;
            case EngagementStatus.Rejected:
                await CommentAsync(2, "Source data is missing for two books.");
                await CommentAsync(5, "The significant variance has no explanation.");
                await repository.TryInsertDecisionAsync(uow, new NewDecision(
                    Guid.NewGuid(), id, 1, "Rejected", p.ReviewerUserId, reviewerName,
                    "Rework the data collection and explain the reconciliation variance.", at), ct);
                break;
        }

        logger.LogInformation("Seeded review history for {EngagementId}", id);
    }
}
