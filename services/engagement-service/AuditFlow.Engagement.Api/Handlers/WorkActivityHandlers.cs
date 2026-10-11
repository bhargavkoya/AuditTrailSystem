using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Telemetry;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Engagement.Api.Data;
using AuditFlow.Engagement.Api.Domain;

namespace AuditFlow.Engagement.Api.Handlers;

/// <summary>
/// Reacts to workflow activity (SectionUpdated, ConfigurationChanged): the first saved edit moves Draft to In Progress and
/// the first edit after a rejection moves Rejected to In Progress, both through the state machine and the guarded
/// <c>WHERE Status = @from</c> update. Events in other editable statuses only refresh the last-activity time; events for
/// locked engagements (Under Review, Approved) are logged and ignored.
/// </summary>
public sealed class WorkActivityHandler(
    IEngagementRepository repository, IOutbox outbox, TimeProvider clock, ILogger<WorkActivityHandler> logger)
{
    public async Task HandleAsync(string engagementId, EventActor? actor, string source, IUnitOfWork uow, CancellationToken ct)
    {
        var engagement = await repository.GetRowAsync(uow, engagementId, ct)
                         ?? throw new InvalidOperationException($"{source} for unknown engagement {engagementId}; retrying.");
        var status = engagement.StatusValue;

        if (!PermissionPolicy.IsEditable(status))
        {
            logger.LogInformation("Ignoring {Source} for {EngagementId}: status is {Status}", source, engagementId, status);
            return;
        }

        var now = clock.GetUtcNow();
        Trigger? trigger = status switch
        {
            EngagementStatus.Draft => Trigger.StartWork,
            EngagementStatus.Rejected => Trigger.StartRework,
            _ => null
        };

        if (trigger is null || !await TryTransitionAsync(engagement, trigger.Value, actor, now, uow, ct))
            await repository.TouchActivityAsync(uow, engagementId, now, ct);
    }

    private async Task<bool> TryTransitionAsync(
        EngagementRow engagement, Trigger trigger, EventActor? actor, DateTimeOffset now, IUnitOfWork uow, CancellationToken ct)
    {
        // The actor was authorized by workflow-service (content.edit); the state machine still requires an editor role.
        Guid? actorId = Guid.TryParse(actor?.UserId, out var id) ? id : null;
        var role = actorId is null ? null : (await repository.GetAsync(engagement.EngagementId, actorId.Value, ct))?.MyRole;

        var decision = EngagementStateMachine.Evaluate(engagement.StatusValue, trigger, new TransitionContext(role));
        if (!decision.Allowed)
        {
            logger.LogWarning("{Trigger} on {EngagementId} denied: {Reason}", trigger, engagement.EngagementId, decision.Reason);
            return false;
        }

        var version = await repository.TransitionAsync(uow, engagement.EngagementId, engagement.StatusValue, decision.To!.Value, now, ct);
        if (version is null) return false; // someone else already moved it

        await outbox.EnqueueAsync(uow, EventEnvelope<EngagementStatusChangedPayload>.Create(
            EventTypes.EngagementStatusChanged, engagement.EngagementId,
            new EngagementStatusChangedPayload(engagement.StatusValue, decision.To.Value, trigger.ToString(), RowVersion.ToToken(version)),
            actor), ct);
        AuditFlowMetrics.RecordTransition(engagement.StatusValue.ToString(), decision.To.Value.ToString());
        return true;
    }
}

public sealed class SectionUpdatedHandler(WorkActivityHandler activity) : IEventHandler<SectionUpdatedPayload>
{
    public Task HandleAsync(EventEnvelope<SectionUpdatedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
        => activity.HandleAsync(envelope.EngagementId, envelope.Actor, EventTypes.SectionUpdated, uow, ct);
}

public sealed class ConfigurationChangedHandler(WorkActivityHandler activity) : IEventHandler<ConfigurationChangedPayload>
{
    public Task HandleAsync(EventEnvelope<ConfigurationChangedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
        => activity.HandleAsync(envelope.EngagementId, envelope.Actor, EventTypes.ConfigurationChanged, uow, ct);
}
