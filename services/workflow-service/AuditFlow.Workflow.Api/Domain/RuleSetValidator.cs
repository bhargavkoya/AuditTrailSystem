using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>Catalogue integrity: a rule set that passes cannot reference unknown sections, fields, facts or operators.</summary>
public static class RuleSetValidator
{
    private static readonly string[] FieldTypeNames =
        [FieldTypes.Checkbox, FieldTypes.Text, FieldTypes.Number, FieldTypes.Date, FieldTypes.Documentation];

    private static readonly string[] DerivedFacts = ["periodType", "region", "recon.hasVariance", "recon.hasSignificant", "recon.hasMissing"];

    public static IReadOnlyList<string> Validate(RuleSet ruleSet)
    {
        var errors = new List<string>();

        void Duplicates<T>(IEnumerable<T> items, string what)
        {
            foreach (var d in items.GroupBy(x => x).Where(g => g.Count() > 1))
                errors.Add($"Duplicate {what} '{d.Key}'.");
        }

        Duplicates(ruleSet.Tabs.Select(t => t.TabNo), "tab");
        Duplicates(ruleSet.Sections.Select(s => s.Key), "section key");

        var knownFacts = new HashSet<string>(DerivedFacts);
        foreach (var section in ruleSet.Sections)
        {
            if (ruleSet.Tabs.All(t => t.TabNo != section.Tab)) errors.Add($"{section.Key}: unknown tab {section.Tab}.");
            Duplicates(section.Fields.Select(f => f.Key), $"field key in {section.Key}");
            foreach (var field in section.Fields)
            {
                knownFacts.Add($"{section.Key}.{field.Key}");
                if (!FieldTypeNames.Contains(field.Type)) errors.Add($"{section.Key}.{field.Key}: unknown field type '{field.Type}'.");
                if (field.Fact is null) continue;

                knownFacts.Add(field.Fact);
                if (section.VisibleWhen is not null) errors.Add($"{section.Key}.{field.Key}: fact fields must live in an always-visible section.");
                if (field.Type != FieldTypes.Checkbox) errors.Add($"{section.Key}.{field.Key}: only checkbox fields may expose a fact.");
            }
        }

        Duplicates(ruleSet.Sections.SelectMany(s => s.SectionRules).Concat(ruleSet.Rules).Select(r => r.Id), "rule id");

        void CheckCondition(Condition? c, string where)
        {
            if (c is null) return;
            if (!RuleEvaluator.KnownOps.Contains(c.Op)) { errors.Add($"{where}: unknown operator '{c.Op}'."); return; }

            var op = c.Op.ToLowerInvariant();
            if (op is "all" or "any")
            {
                if (c.Conditions is not { Count: > 0 }) errors.Add($"{where}: '{c.Op}' needs conditions.");
                foreach (var child in c.Conditions ?? []) CheckCondition(child, where);
                return;
            }
            if (op == "not")
            {
                if (c.Inner is null) errors.Add($"{where}: 'not' needs a condition.");
                CheckCondition(c.Inner, where);
                return;
            }

            if (c.Fact is null || !knownFacts.Contains(c.Fact)) errors.Add($"{where}: unknown fact '{c.Fact}'.");
            if (c.Other is not null && !knownFacts.Contains(c.Other)) errors.Add($"{where}: unknown fact '{c.Other}'.");
            if (op is "equals" or "gt" or "gte" or "lt" or "lte" or "lengthlt" && c.Value is null && c.Other is null)
                errors.Add($"{where}: '{c.Op}' needs a value.");
            if (op == "in" && c.Values is not { Count: > 0 }) errors.Add($"{where}: 'in' needs values.");
        }

        foreach (var section in ruleSet.Sections)
        {
            CheckCondition(section.VisibleWhen, $"{section.Key}.visibleWhen");
            CheckCondition(section.EffectiveAttachment.RequiredWhen, $"{section.Key}.attachment");
            foreach (var field in section.Fields) CheckCondition(field.RequiredWhen, $"{section.Key}.{field.Key}.requiredWhen");
            foreach (var rule in section.SectionRules)
            {
                CheckCondition(rule.When, $"{section.Key}/{rule.Id}");
                if (rule.Field is not null && section.FindField(rule.Field) is null)
                    errors.Add($"{section.Key}/{rule.Id}: unknown field '{rule.Field}'.");
            }
        }

        foreach (var rule in ruleSet.Rules)
        {
            CheckCondition(rule.When, $"rules/{rule.Id}");
            if (rule.Scope is not (IssueScope.Tab or IssueScope.Engagement))
                errors.Add($"rules/{rule.Id}: top-level rules must have Tab or Engagement scope.");
            if (rule.Scope == IssueScope.Tab && ruleSet.Tabs.All(t => t.TabNo != rule.Tab))
                errors.Add($"rules/{rule.Id}: unknown tab.");
        }

        var recon = ruleSet.Reconciliation;
        CheckCondition(recon.EnabledWhen, "reconciliation.enabledWhen");
        if (ruleSet.FindSection(recon.Section) is null) errors.Add($"reconciliation: unknown section '{recon.Section}'.");
        foreach (var pair in recon.Pairs)
        {
            var section = ruleSet.FindSection(pair.Section);
            if (section is null) { errors.Add($"reconciliation.{pair.Key}: unknown section '{pair.Section}'."); continue; }
            foreach (var key in new[] { pair.Expected, pair.Actual })
                if (section.FindField(key)?.Type != FieldTypes.Number)
                    errors.Add($"reconciliation.{pair.Key}: '{key}' must be a number field of {pair.Section}.");
        }
        if (recon.Thresholds.Percent.Warning >= recon.Thresholds.Percent.Significant
            || recon.Thresholds.Value.Warning >= recon.Thresholds.Value.Significant)
            errors.Add("reconciliation: warning thresholds must be below significant thresholds.");

        return errors;
    }
}
