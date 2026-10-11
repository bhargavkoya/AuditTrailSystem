using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>Scope keys for "an attachment exists here" (fed by AttachmentAdded, so no cross-service reads).</summary>
public static class AttachmentScopeKeys
{
    public const string Engagement = "engagement";
    public static string Tab(int tabNo) => $"tab:{tabNo}";
    public static string Section(string sectionKey) => $"section:{sectionKey}";
}

/// <summary>Everything the rules may look at for one engagement.</summary>
/// <param name="Values">sectionKey -> fieldKey -> typed value (hidden sections included; they are ignored).</param>
/// <param name="Attachments">Attachment scope keys that currently have a file.</param>
public sealed record EvaluationContext(
    string PeriodType,
    string Region,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Values,
    IReadOnlySet<string> Attachments);

public sealed record ValidationSummaryResult(IssueCountsDto Counts, bool ReadyToSubmit, string Hash);

/// <param name="Visibility">Section key -> visible.</param>
/// <param name="RequiredFields">(section, field) pairs that are required right now (visible sections only).</param>
/// <param name="AttachmentRequired">Visible sections whose attachment is required right now.</param>
public sealed record WorkflowEvaluation(
    IReadOnlyDictionary<string, bool> Visibility,
    IReadOnlySet<(string Section, string Field)> RequiredFields,
    IReadOnlySet<string> AttachmentRequired,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<ReconciliationRowDto> Reconciliation,
    ValidationSummaryResult Summary)
{
    public bool IsVisible(string sectionKey) => Visibility.TryGetValue(sectionKey, out var v) && v;

    public IssueCountsDto CountsForTab(int tabNo) => Count(Issues.Where(i => i.TabNo == tabNo));

    public static IssueCountsDto Count(IEnumerable<IssueDto> issues)
    {
        var list = issues.ToList();
        return new IssueCountsDto(
            list.Count(i => i.Severity == IssueSeverity.Error),
            list.Count(i => i.Severity == IssueSeverity.Warning),
            list.Count(i => i.Severity == IssueSeverity.Information));
    }
}

/// <summary>
/// The single entry point for "given these values, what is visible, required and wrong?". Pure: no I/O.
/// Order: Tab 1 facts -> visibility -> reconciliation -> derived facts -> required flags and issues.
/// </summary>
public interface IValidationEngine
{
    WorkflowEvaluation Evaluate(RuleSet ruleSet, EvaluationContext context);
}

public sealed class ValidationEngine(IRuleEvaluator evaluator, IReconciliationCalculator reconciliation) : IValidationEngine
{
    public WorkflowEvaluation Evaluate(RuleSet ruleSet, EvaluationContext context)
    {
        var facts = BuildBaseFacts(ruleSet, context);
        var visibility = evaluator.ResolveVisibility(ruleSet, facts);
        bool Visible(string key) => visibility.TryGetValue(key, out var v) && v;

        // Values of hidden sections are kept in storage but invisible to rules.
        foreach (var section in ruleSet.Sections.Where(s => Visible(s.Key)))
            foreach (var field in section.Fields)
                facts.Set($"{section.Key}.{field.Key}", Value(context, section.Key, field.Key));

        var rows = CalculateReconciliation(ruleSet, context, facts, Visible);
        facts.Set("recon.hasVariance", rows.Any(r => r.Indicator is VarianceIndicator.Warning or VarianceIndicator.Significant));
        facts.Set("recon.hasSignificant", rows.Any(r => r.IsSignificant));
        facts.Set("recon.hasMissing", rows.Any(r => r.IsMissing));

        var required = new HashSet<(string, string)>();
        var attachmentRequired = new HashSet<string>();
        var issues = new List<IssueDto>();

        foreach (var section in ruleSet.Sections.Where(s => Visible(s.Key)))
        {
            foreach (var field in section.Fields)
            {
                var value = Value(context, section.Key, field.Key);
                var isRequired = field.Required || (field.RequiredWhen is not null && evaluator.Holds(field.RequiredWhen, facts));
                if (isRequired)
                {
                    required.Add((section.Key, field.Key));
                    if (FieldValues.IsEmpty(value))
                    {
                        var message = field.Type == FieldTypes.Checkbox ? $"{field.Label} must be confirmed." : $"{field.Label} is required.";
                        issues.Add(Issue("FIELD_REQUIRED", IssueSeverity.Error, IssueScope.Field, message, section, field.Key));
                    }
                }

                if (field.MaxLength is { } max && value is string text && text.Length > max)
                    issues.Add(Issue("FIELD_TOO_LONG", IssueSeverity.Error, IssueScope.Field, $"{field.Label} must be at most {max} characters.", section, field.Key));
            }

            var attachment = section.EffectiveAttachment;
            var attachmentIsRequired = attachment.Allowed
                && (attachment.Required || (attachment.RequiredWhen is not null && evaluator.Holds(attachment.RequiredWhen, facts)));
            if (attachmentIsRequired)
            {
                attachmentRequired.Add(section.Key);
                if (!context.Attachments.Contains(AttachmentScopeKeys.Section(section.Key)))
                    issues.Add(Issue("ATTACHMENT_REQUIRED", IssueSeverity.Error, IssueScope.Section, $"{section.Title}: an attachment is required.", section, null));
            }

            foreach (var rule in section.SectionRules.Where(r => evaluator.Holds(r.When, facts)))
            {
                var scope = rule.Field is not null ? IssueScope.Field : rule.Scope;
                issues.Add(Issue(rule.Id, rule.Severity, scope, rule.Message, section, rule.Field));
            }
        }

        foreach (var rule in ruleSet.Rules.Where(r => evaluator.Holds(r.When, facts)))
            issues.Add(new IssueDto(rule.Id, rule.Severity, rule.Scope, rule.Message, rule.Tab, null, null, rule.Severity == IssueSeverity.Error));

        issues.AddRange(ReconciliationIssues(ruleSet, rows));

        var ordered = Order(ruleSet, issues);
        return new WorkflowEvaluation(visibility, required, attachmentRequired, ordered, rows, Summarize(ordered));
    }

    public static Facts BuildBaseFacts(RuleSet ruleSet, EvaluationContext context)
    {
        var facts = Facts.From(new Dictionary<string, object?>
        {
            ["periodType"] = context.PeriodType,
            ["region"] = context.Region
        });
        // Fact-bearing fields live in unconditional sections (checked by the catalogue integrity test).
        foreach (var section in ruleSet.Sections)
            foreach (var field in section.Fields.Where(f => f.Fact is not null))
                facts.Set(field.Fact!, Value(context, section.Key, field.Key));
        return facts;
    }

    private IReadOnlyList<ReconciliationRowDto> CalculateReconciliation(
        RuleSet ruleSet, EvaluationContext context, Facts facts, Func<string, bool> visible)
    {
        var def = ruleSet.Reconciliation;
        if (!visible(def.Section) || !evaluator.Holds(def.EnabledWhen, facts))
            return [];

        var inputs = def.Pairs
            .Where(p => visible(p.Section))
            .Select(p => new ReconciliationInput(
                p, Value(context, p.Section, p.Expected) as decimal?, Value(context, p.Section, p.Actual) as decimal?));
        return reconciliation.Calculate(def.Thresholds, inputs);
    }

    private static IEnumerable<IssueDto> ReconciliationIssues(RuleSet ruleSet, IReadOnlyList<ReconciliationRowDto> rows)
    {
        var section = ruleSet.FindSection(ruleSet.Reconciliation.Section);
        if (section is null) yield break;

        foreach (var row in rows)
        {
            if (row.IsMissing)
                yield return Issue("RECON_VALUE_MISSING", IssueSeverity.Error, IssueScope.Section,
                    $"{row.Label}: a book or ledger value is missing, so the comparison cannot be made.", section, null);
            else if (row.Indicator == VarianceIndicator.Warning)
                yield return Issue("RECON_VARIANCE_WARNING", IssueSeverity.Warning, IssueScope.Section, Describe(row, "Variance"), section, null);
            else if (row.IsSignificant)
                yield return Issue("RECON_VARIANCE_SIGNIFICANT", IssueSeverity.Error, IssueScope.Section, Describe(row, "Significant variance"), section, null);
        }
    }

    private static string Describe(ReconciliationRowDto row, string prefix)
    {
        var percent = row.PercentVariance is { } p ? $"{p.ToString("0.##", CultureInfo.InvariantCulture)}%" : "n/a";
        var value = row.ValueVariance?.ToString("N0", CultureInfo.InvariantCulture);
        return $"{row.Label}: {prefix.ToLowerInvariant()} of {percent} ({value}) between book and ledger value.";
    }

    private static IssueDto Issue(string code, IssueSeverity severity, IssueScope scope, string message, SectionDefinition section, string? field)
        => new(code, severity, scope, message, section.Tab, section.Key, field, severity == IssueSeverity.Error);

    private static object? Value(EvaluationContext context, string section, string field)
        => context.Values.TryGetValue(section, out var fields) && fields.TryGetValue(field, out var value) ? value : null;

    private static IReadOnlyList<IssueDto> Order(RuleSet ruleSet, IEnumerable<IssueDto> issues)
    {
        var sectionOrder = ruleSet.Sections.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i);
        return issues
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.TabNo ?? int.MaxValue)
            .ThenBy(i => i.SectionKey is not null ? sectionOrder.GetValueOrDefault(i.SectionKey, int.MaxValue) : -1)
            .ThenBy(i => i.FieldKey, StringComparer.Ordinal)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static ValidationSummaryResult Summarize(IReadOnlyList<IssueDto> issues)
    {
        var counts = WorkflowEvaluation.Count(issues);
        var text = string.Join('\n', issues.Select(i =>
            $"{i.Code}|{i.Severity}|{i.Scope}|{i.TabNo}|{i.SectionKey}|{i.FieldKey}|{i.Message}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return new ValidationSummaryResult(counts, counts.Errors == 0, hash);
    }
}
