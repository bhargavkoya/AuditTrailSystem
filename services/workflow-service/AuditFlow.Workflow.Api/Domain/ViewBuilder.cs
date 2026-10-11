using AuditFlow.BuildingBlocks.Versioning;
using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Data;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>
/// Turns stored state + the evaluation + the caller's capabilities into the exact view model the UI renders.
/// Everything the client needs to know (visible, editable, required, can upload, issues) is decided here.
/// </summary>
public static class ViewBuilder
{
    public static TabsDto BuildTabs(RuleSet ruleSet, WorkflowEvaluation evaluation)
        => new(true, ruleSet.Tabs.Select(t => new TabSummaryDto(t.TabNo, t.Title, evaluation.CountsForTab(t.TabNo))).ToList());

    public static TabViewDto BuildTab(
        RuleSet ruleSet, WorkflowSnapshot snapshot, WorkflowEvaluation evaluation, EngagementAccessDto access, int tabNo)
    {
        var tab = ruleSet.Tabs.Single(t => t.TabNo == tabNo);
        var sections = ruleSet.Sections
            .Where(s => s.Tab == tabNo && evaluation.IsVisible(s.Key))
            .OrderBy(s => s.Order)
            .Select(s => BuildSection(s, snapshot, evaluation, access))
            .ToList();

        var reconciliationTab = ruleSet.FindSection(ruleSet.Reconciliation.Section)?.Tab;
        return new TabViewDto(
            tabNo, tab.Title, access.Capabilities.Contains(Capabilities.Edit), sections,
            evaluation.Issues.Where(i => i.TabNo == tabNo && i.SectionKey is null).ToList(),
            reconciliationTab == tabNo ? evaluation.Reconciliation : null);
    }

    public static SectionViewDto BuildSection(
        SectionDefinition section, WorkflowSnapshot snapshot, WorkflowEvaluation evaluation, EngagementAccessDto access)
    {
        var editable = access.Capabilities.Contains(Capabilities.Edit);
        var values = snapshot.Values.GetValueOrDefault(section.Key);

        var fields = section.Fields.Select(f => new FieldViewDto(
            f.Key, f.Type, f.Label,
            FieldValues.ToView(values is not null && values.TryGetValue(f.Key, out var v) ? v : null),
            evaluation.RequiredFields.Contains((section.Key, f.Key)),
            editable,
            evaluation.Issues.Where(i => i.SectionKey == section.Key && i.FieldKey == f.Key).ToList())).ToList();

        // Roles without attachment visibility (Viewer) get no attachment information at all.
        SectionAttachmentDto? attachment = null;
        if (section.EffectiveAttachment.Allowed && access.Capabilities.Contains(Capabilities.AttachView))
        {
            attachment = new SectionAttachmentDto(
                Allowed: true,
                Required: evaluation.AttachmentRequired.Contains(section.Key),
                CanUpload: access.Capabilities.Contains(Capabilities.Attach),
                HasCurrent: snapshot.Attachments.Contains(AttachmentScopeKeys.Section(section.Key)));
        }

        return new SectionViewDto(
            section.Key, section.Title, editable, RowVersion.ToToken(snapshot.Sections[section.Key].RowVer), fields, attachment,
            evaluation.Issues.Where(i => i.SectionKey == section.Key && i.FieldKey is null).ToList());
    }

    public static ValidationSummaryDto BuildSummary(WorkflowEvaluation evaluation)
        => new(evaluation.Summary.Counts, evaluation.Summary.ReadyToSubmit, evaluation.Issues, evaluation.Reconciliation);
}
