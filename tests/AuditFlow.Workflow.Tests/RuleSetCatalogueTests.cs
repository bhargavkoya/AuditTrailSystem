using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Tests;

public class RuleSetCatalogueTests
{
    private static RuleSet Load() => RuleSetJson.LoadEmbedded();

    [Fact]
    public void Shipped_rule_set_passes_the_integrity_check() => Assert.Empty(RuleSetValidator.Validate(Load()));

    [Fact]
    public void Shipped_rule_set_has_five_tabs_and_thirteen_sections()
    {
        var ruleSet = Load();

        Assert.Equal([1, 2, 3, 4, 5], ruleSet.Tabs.Select(t => t.TabNo));
        Assert.Equal(13, ruleSet.Sections.Count);
        Assert.All(ruleSet.Tabs, t => Assert.Contains(ruleSet.Sections, s => s.Tab == t.TabNo));
    }

    [Fact]
    public void Tab_one_is_unconditional_and_exposes_the_rule_inputs()
    {
        var section = Load().FindSection("section-1-1")!;

        Assert.Null(section.VisibleWhen);
        Assert.Equal(
            ["cfg.equities", "cfg.fixedIncome", "cfg.derivatives", "cfg.fx", "opt.reconciliation", "opt.legalReview"],
            section.Fields.Where(f => f.Fact is not null).Select(f => f.Fact!));
    }

    [Fact]
    public void Every_field_type_in_the_catalogue_is_supported()
    {
        var types = Load().Sections.SelectMany(s => s.Fields).Select(f => f.Type).Distinct();

        Assert.All(types, t => Assert.Contains(t, new[] { FieldTypes.Checkbox, FieldTypes.Text, FieldTypes.Number, FieldTypes.Date, FieldTypes.Documentation }));
    }

    private static RuleSet With(Func<RuleSet, RuleSet> change) => change(Load());


    private static RuleSet WithSection(RuleSet r, string key, Func<SectionDefinition, SectionDefinition> change)
        => r with { Sections = r.Sections.Select(s => s.Key == key ? change(s) : s).ToList() };

    [Fact]
    public void Validator_catches_duplicate_section_keys()
    {
        var ruleSet = With(r => r with { Sections = [.. r.Sections, r.Sections[0]] });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("Duplicate section key"));
    }

    [Fact]
    public void Validator_catches_a_rule_targeting_an_unknown_field()
    {
        var ruleSet = WithSection(Load(), "section-4-1", s => s with
        {
            Rules = [.. s.SectionRules, s.SectionRules[0] with { Id = "BAD", Field = "nope" }]
        });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("unknown field 'nope'"));
    }

    [Fact]
    public void Validator_catches_unknown_facts_and_operators()
    {
        var ruleSet = WithSection(Load(), "section-2-2", s => s with { VisibleWhen = new Condition("isTrue", Fact: "cfg.unknown") });
        var badOp = WithSection(Load(), "section-2-2", s => s with { VisibleWhen = new Condition("sometimes", Fact: "cfg.equities") });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("unknown fact 'cfg.unknown'"));
        Assert.Contains(RuleSetValidator.Validate(badOp), e => e.Contains("unknown operator 'sometimes'"));
    }

    [Fact]
    public void Validator_catches_a_comparison_without_an_operand()
    {
        var ruleSet = WithSection(Load(), "section-2-2", s => s with { VisibleWhen = new Condition("gt", Fact: "cfg.equities") });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("needs a value"));
    }

    [Fact]
    public void Validator_requires_fact_fields_in_always_visible_sections()
    {
        var ruleSet = WithSection(Load(), "section-1-1", s => s with { VisibleWhen = new Condition("isTrue", Fact: "periodType") });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("always-visible"));
    }

    [Fact]
    public void Validator_catches_bad_reconciliation_pairs_and_thresholds()
    {
        var pairs = With(r => r with
        {
            Reconciliation = r.Reconciliation with { Pairs = [new ReconciliationPair("x", "X", "section-2-2", "bookValue", "missingField")] }
        });
        var thresholds = With(r => r with
        {
            Reconciliation = r.Reconciliation with { Thresholds = new ReconciliationThresholds(new ThresholdLevels(5, 1), new ThresholdLevels(1, 2)) }
        });

        Assert.Contains(RuleSetValidator.Validate(pairs), e => e.Contains("must be a number field"));
        Assert.Contains(RuleSetValidator.Validate(thresholds), e => e.Contains("thresholds"));
    }

    [Fact]
    public void Validator_catches_a_section_on_an_unknown_tab()
    {
        var ruleSet = WithSection(Load(), "section-4-1", s => s with { Tab = 9 });

        Assert.Contains(RuleSetValidator.Validate(ruleSet), e => e.Contains("unknown tab 9"));
    }
}
