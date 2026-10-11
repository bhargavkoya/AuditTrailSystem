using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>
/// Turns EngagementCreated into the engagement's workflow: configuration, one row per section (visibility decided by the
/// rules), one row per field, and the first validation summary. Runs inside the consumer's transaction and is idempotent.
/// </summary>
public sealed class ProvisioningService(
    IWorkflowRepository repository,
    IRuleSetProvider ruleSets,
    IValidationEngine engine,
    IRuleEvaluator evaluator,
    TimeProvider clock,
    ILogger<ProvisioningService> logger)
{
    public async Task ProvisionAsync(EventEnvelope<EngagementCreatedPayload> envelope, IUnitOfWork uow, CancellationToken ct)
    {
        var engagementId = envelope.EngagementId;
        var payload = envelope.Payload;

        if (await repository.LockAsync(uow, engagementId, ct))
        {
            logger.LogInformation("{EngagementId} is already provisioned; ignoring duplicate EngagementCreated", engagementId);
            return;
        }

        var ruleSet = await ruleSets.GetActiveAsync(ct);
        var values = ruleSet.Sections.ToDictionary(
            s => s.Key,
            s => s.Fields.ToDictionary(f => f.Key, f => f.Type == FieldTypes.Checkbox ? (object?)false : null));

        ApplyCreationChoices(ruleSet, payload.Configuration, values);

        if (payload.Seeded)
        {
            var owner = payload.Participants.FirstOrDefault(p => p.UserId == payload.OwnerUserId)?.DisplayName ?? "Auditor";
            SampleDataGenerator.Fill(values, ruleSet, payload.Status, payload.PeriodType, payload.Region, owner, envelope.OccurredAt, evaluator);
        }

        var presence = await repository.GetAttachmentPresenceAsync(uow, engagementId, ct);
        var context = new EvaluationContext(
            payload.PeriodType, payload.Region,
            values.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)kv.Value), presence);
        var evaluation = engine.Evaluate(ruleSet, context);

        var sections = ruleSet.Sections.Select(s => new SectionInit(s.Key, s.Tab, evaluation.IsVisible(s.Key))).ToList();
        var fields = ruleSet.Sections.SelectMany(s => s.Fields.Select(f =>
            new FieldInit(s.Key, f.Key, f.Type, FieldValues.ToStored(f.Type, values[s.Key][f.Key])))).ToList();

        await repository.InsertProvisionedAsync(uow, new ProvisionedEngagement(
            engagementId, payload.PeriodType, payload.Region, ruleSet.Version, clock.GetUtcNow(), sections, fields, evaluation.Summary), ct);

        logger.LogInformation("Provisioned {EngagementId} on rule set v{Version}: {Sections} sections, {Errors} errors",
            engagementId, ruleSet.Version, sections.Count, evaluation.Summary.Counts.Errors);
    }

    /// <summary>Tab 1 fields declare their source ("config:Equities" / "option:includeReconciliation").</summary>
    private static void ApplyCreationChoices(RuleSet ruleSet, ConfigurationInfo configuration, Dictionary<string, Dictionary<string, object?>> values)
    {
        foreach (var section in ruleSet.Sections)
            foreach (var field in section.Fields.Where(f => f.Source is not null))
            {
                var source = field.Source!;
                var separator = source.IndexOf(':');
                var kind = source[..separator];
                var name = source[(separator + 1)..];

                values[section.Key][field.Key] = kind switch
                {
                    "config" => configuration.ConfigTypes.Contains(name, StringComparer.OrdinalIgnoreCase),
                    "option" => configuration.Options.TryGetValue(name, out var on) && on,
                    _ => throw new InvalidOperationException($"Unknown field source '{source}'.")
                };
            }
    }
}
