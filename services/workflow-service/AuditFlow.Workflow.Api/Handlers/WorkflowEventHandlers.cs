using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Api.Handlers;

/// <summary>
/// AttachmentAdded: remember that the scope now has a file (so "attachment required" rules can be satisfied without reading
/// the attachment database) and re-evaluate. The presence row is kept even if the engagement is not provisioned yet.
/// </summary>
public sealed class AttachmentAddedHandler(
    IWorkflowRepository repository, IRuleSetProvider ruleSets, Recomputer recomputer, TimeProvider clock,
    ILogger<AttachmentAddedHandler> logger) : IEventHandler<AttachmentAddedPayload>
{
    public async Task HandleAsync(EventEnvelope<AttachmentAddedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
    {
        var p = envelope.Payload;
        var scopeKey = p.Scope switch
        {
            AttachmentScope.Engagement => AttachmentScopeKeys.Engagement,
            AttachmentScope.Tab when p.TabNo is { } tab => AttachmentScopeKeys.Tab(tab),
            AttachmentScope.Section when p.SectionKey is { } section => AttachmentScopeKeys.Section(section),
            _ => throw new InvalidOperationException($"Malformed AttachmentAdded scope {p.Scope}.")
        };

        if (!await repository.LockAsync(uow, envelope.EngagementId, ct))
        {
            // Provisioning will pick the presence row up when EngagementCreated arrives.
            await repository.AddAttachmentPresenceAsync(uow, envelope.EngagementId, scopeKey, clock.GetUtcNow(), ct);
            logger.LogInformation("AttachmentAdded for unprovisioned {EngagementId} recorded", envelope.EngagementId);
            return;
        }

        await repository.AddAttachmentPresenceAsync(uow, envelope.EngagementId, scopeKey, clock.GetUtcNow(), ct);
        var snapshot = await repository.LoadAsync(uow.Connection, uow.Transaction, envelope.EngagementId, ct);
        var ruleSet = await ruleSets.GetAsync(snapshot!.RuleSetVersion, ct);
        await recomputer.RecomputeAsync(uow, envelope.EngagementId, ruleSet, configurationChanged: false, envelope.Actor, ct);
    }
}

/// <summary>
/// EngagementUpdated: period type and region are rule inputs. Refresh them and re-evaluate; announce ConfigurationChanged
/// when that flips any section. Throws when the engagement is not provisioned yet so the broker redelivers.
/// </summary>
public sealed class EngagementUpdatedHandler(
    IWorkflowRepository repository, IRuleSetProvider ruleSets, Recomputer recomputer) : IEventHandler<EngagementUpdatedPayload>
{
    public async Task HandleAsync(EventEnvelope<EngagementUpdatedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
    {
        var id = envelope.EngagementId;
        if (!await repository.LockAsync(uow, id, ct))
            throw new InvalidOperationException($"EngagementUpdated for {id} arrived before it was provisioned; retrying.");

        var snapshot = (await repository.LoadAsync(uow.Connection, uow.Transaction, id, ct))!;
        var periodType = envelope.Payload.PeriodType ?? snapshot.PeriodType;
        var region = envelope.Payload.Region ?? snapshot.Region;

        var changedKeys = new List<string>();
        if (periodType != snapshot.PeriodType) changedKeys.Add("periodType");
        if (region != snapshot.Region) changedKeys.Add("region");
        if (changedKeys.Count == 0) return;

        await repository.UpdateRuleInputsAsync(uow, id, periodType, region, ct);
        var ruleSet = await ruleSets.GetAsync(snapshot.RuleSetVersion, ct);
        var result = await recomputer.RecomputeAsync(uow, id, ruleSet, configurationChanged: true, envelope.Actor, ct);
        await recomputer.EnqueueConfigurationChangedAsync(uow, id, result, changedKeys, envelope.Actor, ct);
    }
}
