using System.Text.Json;
using AuditFlow.BuildingBlocks.Access;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using AuditFlow.Workflow.Api.Data;

namespace AuditFlow.Workflow.Api.Domain;

/// <param name="Version">The section version the caller last saw (opaque token).</param>
public sealed record SectionPatch(string SectionKey, string Version, IReadOnlyDictionary<string, JsonElement> Fields);

public sealed record SaveResult(
    RuleSet RuleSet,
    WorkflowSnapshot Snapshot,
    WorkflowEvaluation Evaluation,
    EngagementAccessDto Access,
    IReadOnlyList<VisibilityChangeDto> VisibilityChanges);

/// <summary>Reads and writes of the workflow. Every call is authorized through engagement-service's access answer.</summary>
public sealed class WorkflowService(
    IWorkflowRepository repository,
    IRuleSetProvider ruleSets,
    IValidationEngine engine,
    IEngagementAccessClient accessClient,
    IDbConnectionFactory connections,
    IUnitOfWorkFactory unitOfWork,
    IOutbox outbox,
    Recomputer recomputer,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    // ---- Reads ----------------------------------------------------------------------------------------------

    /// <summary><c>Ready = false</c> while the workflow is still being provisioned from EngagementCreated.</summary>
    public async Task<TabsDto> GetTabsAsync(string engagementId, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.View, ct);
        var loaded = await LoadEvaluatedAsync(engagementId, ct);
        return loaded is null ? new TabsDto(false, []) : ViewBuilder.BuildTabs(loaded.Value.RuleSet, loaded.Value.Evaluation);
    }

    public async Task<TabViewDto> GetTabAsync(string engagementId, int tabNo, CancellationToken ct)
    {
        var access = await accessClient.RequireAsync(engagementId, Capabilities.View, ct);
        var (ruleSet, snapshot, evaluation) = await LoadEvaluatedAsync(engagementId, ct) ?? throw NotProvisioned();
        RequireTab(ruleSet, tabNo);
        return ViewBuilder.BuildTab(ruleSet, snapshot, evaluation, access, tabNo);
    }

    public async Task<ValidationSummaryDto> GetValidationSummaryAsync(string engagementId, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.View, ct);
        var (_, _, evaluation) = await LoadEvaluatedAsync(engagementId, ct) ?? throw NotProvisioned();
        return ViewBuilder.BuildSummary(evaluation);
    }

    /// <summary>Internal: consumed by engagement-service when submitting (Phase 3).</summary>
    public async Task<SubmissionReadinessDto> GetSubmissionReadinessAsync(string engagementId, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.View, ct);
        var loaded = await LoadEvaluatedAsync(engagementId, ct);
        if (loaded is null) return new SubmissionReadinessDto(false, []);

        var evaluation = loaded.Value.Evaluation;
        return new SubmissionReadinessDto(
            evaluation.Summary.ReadyToSubmit, evaluation.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList());
    }

    /// <summary>Internal: consumed by attachment-service to validate a section-scoped upload.</summary>
    public async Task<SectionPolicyDto> GetSectionPolicyAsync(string engagementId, string sectionKey, CancellationToken ct)
    {
        await accessClient.RequireAsync(engagementId, Capabilities.View, ct);
        var loaded = await LoadEvaluatedAsync(engagementId, ct);
        var section = loaded?.RuleSet.FindSection(sectionKey);
        if (loaded is null || section is null) return new SectionPolicyDto(false, false, 0, false);

        return new SectionPolicyDto(true, loaded.Value.Evaluation.IsVisible(sectionKey), section.Tab, section.EffectiveAttachment.Allowed);
    }

    // ---- Writes ---------------------------------------------------------------------------------------------

    public async Task<SaveSectionResultDto> SaveSectionAsync(
        string engagementId, string sectionKey, string version, IReadOnlyDictionary<string, JsonElement> fields, CancellationToken ct)
    {
        var result = await SaveAsync(engagementId, [new SectionPatch(sectionKey, version, fields)], tabNo: null, ct);
        var section = result.RuleSet.FindSection(sectionKey)!;
        return new SaveSectionResultDto(
            ViewBuilder.BuildSection(section, result.Snapshot, result.Evaluation, result.Access),
            ViewBuilder.BuildSummary(result.Evaluation), result.VisibilityChanges);
    }

    public async Task<SaveTabResultDto> SaveTabAsync(string engagementId, int tabNo, IReadOnlyList<SectionPatch> patches, CancellationToken ct)
    {
        var result = await SaveAsync(engagementId, patches, tabNo, ct);
        return new SaveTabResultDto(
            ViewBuilder.BuildTab(result.RuleSet, result.Snapshot, result.Evaluation, result.Access, tabNo),
            ViewBuilder.BuildSummary(result.Evaluation), result.VisibilityChanges);
    }

    /// <summary>
    /// One transaction: lock the engagement, check every section version, write only what changed, re-evaluate the whole
    /// engagement, persist visibility and the summary, and write events to the outbox. A stale version anywhere rolls
    /// everything back (409). A save that changes nothing writes nothing and emits nothing.
    /// </summary>
    private async Task<SaveResult> SaveAsync(string engagementId, IReadOnlyList<SectionPatch> patches, int? tabNo, CancellationToken ct)
    {
        var access = await accessClient.RequireAsync(engagementId, Capabilities.Edit, ct);

        var current = await LoadEvaluatedAsync(engagementId, ct) ?? throw NotProvisioned();
        var parsed = ParsePatches(current.RuleSet, patches, tabNo);

        await using var uow = await unitOfWork.BeginAsync(ct);
        if (!await repository.LockAsync(uow, engagementId, ct)) throw NotProvisioned();

        var ruleSet = current.RuleSet;
        var before = await LoadAsync(uow, engagementId, ct);
        var beforeEvaluation = engine.Evaluate(ruleSet, before.ToContext());

        var writes = new List<(ParsedPatch Patch, List<FieldWrite> Writes, List<string> Changed)>();
        foreach (var patch in parsed)
        {
            var state = before.Sections[patch.Section.Key];
            if (!beforeEvaluation.IsVisible(patch.Section.Key))
                throw new ValidationFailedException(patch.Section.Key, "This section is not available for the current configuration.");
            if (!state.RowVer.AsSpan().SequenceEqual(patch.ExpectedVersion))
                throw new ConcurrencyException(RowVersion.ToToken(state.RowVer), patch.Section.Key);

            var changed = new List<string>();
            var fieldWrites = new List<FieldWrite>();
            foreach (var (fieldKey, value) in patch.Values)
            {
                var field = patch.Section.FindField(fieldKey)!;
                if (SameValue(before.Values[patch.Section.Key].GetValueOrDefault(fieldKey), value)) continue;
                changed.Add(fieldKey);
                fieldWrites.Add(new FieldWrite(patch.Section.Key, fieldKey, field.Type, FieldValues.ToStored(field.Type, value)));
            }
            if (changed.Count > 0) writes.Add((patch, fieldWrites, changed));
        }

        if (writes.Count == 0)
            return new SaveResult(ruleSet, before, beforeEvaluation, access, []);

        var now = clock.GetUtcNow();
        foreach (var (patch, fieldWrites, _) in writes)
        {
            if (!await repository.WriteSectionAsync(uow, engagementId, patch.Section.Key, patch.ExpectedVersion, fieldWrites,
                    currentUser.UserId, currentUser.Name, now, ct))
                throw new ConcurrencyException(resource: patch.Section.Key);
        }

        var factKeys = writes.SelectMany(w => w.Changed.Where(k => w.Patch.Section.FindField(k)!.Fact is not null)).ToList();
        var actor = new EventActor(currentUser.UserId.ToString(), currentUser.Name);
        var recomputed = await recomputer.RecomputeAsync(uow, engagementId, ruleSet, configurationChanged: factKeys.Count > 0, actor, ct);

        foreach (var (patch, _, changed) in writes)
            await outbox.EnqueueAsync(uow, EventEnvelope<SectionUpdatedPayload>.Create(
                EventTypes.SectionUpdated, engagementId,
                new SectionUpdatedPayload(patch.Section.Tab, patch.Section.Key, changed,
                    RowVersion.ToToken(recomputed.Snapshot.Sections[patch.Section.Key].RowVer)),
                actor), ct);

        if (factKeys.Count > 0 || recomputed.VisibilityChanges.Count > 0)
            await recomputer.EnqueueConfigurationChangedAsync(uow, engagementId, recomputed, factKeys, actor, ct);

        await uow.CommitAsync(ct);
        return new SaveResult(ruleSet, recomputed.Snapshot, recomputed.Evaluation, access, recomputed.VisibilityChanges);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private sealed record ParsedPatch(SectionDefinition Section, byte[] ExpectedVersion, IReadOnlyDictionary<string, object?> Values);

    /// <summary>Shape and type checks that need no database state: unknown sections / fields, wrong types, bad versions.</summary>
    private static List<ParsedPatch> ParsePatches(RuleSet ruleSet, IReadOnlyList<SectionPatch> patches, int? tabNo)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = [];
            list.Add(message);
        }

        if (patches.Count == 0) throw new ValidationFailedException("sections", "At least one section is required.");
        if (tabNo is { } tab) RequireTab(ruleSet, tab);

        var result = new List<ParsedPatch>();
        foreach (var patch in patches)
        {
            var section = ruleSet.FindSection(patch.SectionKey);
            if (section is null) { Error(patch.SectionKey, "Unknown section."); continue; }
            if (tabNo is not null && section.Tab != tabNo) { Error(patch.SectionKey, $"Section does not belong to tab {tabNo}."); continue; }
            if (result.Any(r => r.Section.Key == section.Key)) { Error(patch.SectionKey, "Section appears more than once."); continue; }

            var values = new Dictionary<string, object?>();
            foreach (var (fieldKey, json) in patch.Fields ?? new Dictionary<string, JsonElement>())
            {
                var field = section.FindField(fieldKey);
                if (field is null) { Error($"{section.Key}.{fieldKey}", "Unknown field."); continue; }
                var parsedValue = FieldValues.Parse(field, json);
                if (!parsedValue.IsValid) Error($"{section.Key}.{fieldKey}", parsedValue.Error!);
                else values[fieldKey] = parsedValue.Value;
            }

            result.Add(new ParsedPatch(section, RowVersion.FromToken(patch.Version), values));
        }

        if (errors.Count > 0)
            throw new ValidationFailedException(errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        return result;
    }

    private static bool SameValue(object? current, object? incoming) => (current, incoming) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        _ => Equals(current, incoming)
    };

    private static void RequireTab(RuleSet ruleSet, int tabNo)
    {
        if (ruleSet.Tabs.All(t => t.TabNo != tabNo)) throw new NotFoundException($"Tab {tabNo} does not exist.");
    }

    private static NotFoundException NotProvisioned()
        => new("The workflow for this engagement is still being set up. Try again in a moment.");

    private async Task<WorkflowSnapshot> LoadAsync(IUnitOfWork uow, string engagementId, CancellationToken ct)
        => await repository.LoadAsync(uow.Connection, uow.Transaction, engagementId, ct) ?? throw NotProvisioned();

    private async Task<(RuleSet RuleSet, WorkflowSnapshot Snapshot, WorkflowEvaluation Evaluation)?> LoadEvaluatedAsync(
        string engagementId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var snapshot = await repository.LoadAsync(connection, null, engagementId, ct);
        if (snapshot is null) return null;

        var ruleSet = await ruleSets.GetAsync(snapshot.RuleSetVersion, ct);
        return (ruleSet, snapshot, engine.Evaluate(ruleSet, snapshot.ToContext()));
    }
}
