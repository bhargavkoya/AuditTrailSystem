using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;

namespace AuditFlow.Workflow.Api.Domain;

public sealed record RecomputeResult(
    WorkflowSnapshot Snapshot,
    WorkflowEvaluation Evaluation,
    IReadOnlyList<VisibilityChangeDto> VisibilityChanges);

/// <summary>
/// Re-evaluates a whole engagement inside the caller's transaction (which must hold the engagement lock) and persists what
/// changed: section visibility, the configuration version, and the published validation summary
/// (ValidationSummaryChanged is only written when the summary hash moved). Shared by saves and by event consumers.
/// </summary>
public sealed class Recomputer(IWorkflowRepository repository, IValidationEngine engine, IOutbox outbox, TimeProvider clock)
{
    public async Task<RecomputeResult> RecomputeAsync(
        IUnitOfWork uow, string engagementId, RuleSet ruleSet, bool configurationChanged, EventActor? actor, CancellationToken ct)
    {
        var snapshot = await LoadAsync(uow, engagementId, ct);
        var evaluation = engine.Evaluate(ruleSet, snapshot.ToContext());
        var now = clock.GetUtcNow();

        var flips = ruleSet.Sections
            .Where(s => snapshot.Sections[s.Key].IsVisible != evaluation.IsVisible(s.Key))
            .Select(s => new VisibilityChangeDto(s.Key, evaluation.IsVisible(s.Key)))
            .ToList();
        foreach (var flip in flips)
            await repository.SetVisibilityAsync(uow, engagementId, flip.SectionKey, flip.Visible, now, ct);

        if (configurationChanged || flips.Count > 0)
            await repository.TouchConfigurationAsync(uow, engagementId, ct);

        var summary = evaluation.Summary;
        if (summary.Hash != snapshot.PublishedSummaryHash)
        {
            await repository.UpsertSummaryAsync(uow, engagementId, summary, now, ct);
            await outbox.EnqueueAsync(uow, EventEnvelope<ValidationSummaryChangedPayload>.Create(
                EventTypes.ValidationSummaryChanged, engagementId,
                new ValidationSummaryChangedPayload(summary.Counts.Errors, summary.Counts.Warnings, summary.Counts.Infos, summary.ReadyToSubmit, summary.Hash),
                actor), ct);
        }

        var final = flips.Count > 0 || configurationChanged ? await LoadAsync(uow, engagementId, ct) : snapshot;
        return new RecomputeResult(final, evaluation, flips);
    }

    public async Task EnqueueConfigurationChangedAsync(
        IUnitOfWork uow, string engagementId, RecomputeResult result, IReadOnlyList<string> changedKeys, EventActor? actor, CancellationToken ct)
        => await outbox.EnqueueAsync(uow, EventEnvelope<ConfigurationChangedPayload>.Create(
            EventTypes.ConfigurationChanged, engagementId,
            new ConfigurationChangedPayload(
                changedKeys,
                result.VisibilityChanges.Select(v => new AffectedSection(v.SectionKey, v.Visible)).ToList(),
                RowVersion.ToToken(result.Snapshot.ConfigVersion)),
            actor), ct);

    private async Task<WorkflowSnapshot> LoadAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => await repository.LoadAsync(uow.Connection, uow.Transaction, engagementId, ct)
           ?? throw new InvalidOperationException($"Workflow for {engagementId} is not provisioned.");
}
