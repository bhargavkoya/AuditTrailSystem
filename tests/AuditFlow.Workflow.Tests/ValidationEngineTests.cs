using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Tests;

/// <summary>Fluent builder for evaluation contexts against the shipped rule set.</summary>
internal sealed class Scenario
{
    private readonly Dictionary<string, Dictionary<string, object?>> values = new();
    private readonly HashSet<string> attachments = [];

    public string PeriodType { get; set; } = "Annual";
    public string Region { get; set; } = "EMEA";

    public Scenario Set(string section, string field, object? value)
    {
        if (!values.TryGetValue(section, out var fields)) values[section] = fields = new();
        fields[field] = value;
        return this;
    }

    public Scenario Config(bool equities = false, bool fixedIncome = false, bool derivatives = false, bool fx = false,
        bool reconciliation = false, bool legalReview = false)
    {
        Set("section-1-1", "cfgEquities", equities).Set("section-1-1", "cfgFixedIncome", fixedIncome)
            .Set("section-1-1", "cfgDerivatives", derivatives).Set("section-1-1", "cfgFx", fx)
            .Set("section-1-1", "optReconciliation", reconciliation).Set("section-1-1", "optLegalReview", legalReview);
        return this;
    }

    public Scenario Attach(string scopeKey)
    {
        attachments.Add(scopeKey);
        return this;
    }

    public EvaluationContext Build() => new(
        PeriodType, Region,
        values.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)kv.Value),
        attachments);
}

public class ValidationEngineTests
{
    private static readonly RuleSet RuleSet = RuleSetJson.LoadEmbedded();
    private static readonly ValidationEngine Engine = new(new RuleEvaluator(), new ReconciliationCalculator());

    private static WorkflowEvaluation Run(Scenario s) => Engine.Evaluate(RuleSet, s.Build());

    private static IEnumerable<IssueDto> For(WorkflowEvaluation e, string code) => e.Issues.Where(i => i.Code == code);

    // A scenario with every always-visible requirement satisfied for the sections it names.
    private static Scenario Complete()
    {
        return new Scenario()
            .Config(equities: true)
            .Set("section-1-1", "periodStart", new DateOnly(2025, 1, 1))
            .Set("section-1-1", "periodEnd", new DateOnly(2025, 12, 31))
            .Set("section-2-1", "sourceSystem", "GL").Set("section-2-1", "extractDate", new DateOnly(2026, 1, 5))
            .Set("section-2-1", "recordCount", 10m)
            .Set("section-2-2", "bookValue", 1_000_000m)
            .Set("section-3-1", "proceduresCompleted", true).Set("section-3-1", "performedOn", new DateOnly(2026, 1, 20))
            .Set("section-4-1", "summary", "The audit was completed and the position was agreed to the ledger without exception.")
            .Attach(AttachmentScopeKeys.Section("section-2-1"));
    }

    [Fact]
    public void A_complete_engagement_is_ready_with_no_issues()
    {
        var result = Run(Complete());

        Assert.Empty(result.Issues.Where(i => i.Severity != IssueSeverity.Information));
        Assert.True(result.Summary.ReadyToSubmit);
        Assert.Equal(0, result.Summary.Counts.Errors);
    }

    [Fact]
    public void An_empty_engagement_reports_required_field_errors_and_is_not_ready()
    {
        var result = Run(new Scenario().Config());

        Assert.False(result.Summary.ReadyToSubmit);
        Assert.Contains(result.Issues, i => i.Code == "CONFIG_TYPE_REQUIRED" && i.Severity == IssueSeverity.Error && i.Scope == IssueScope.Section);
        Assert.Contains(result.Issues, i => i.Code == "FIELD_REQUIRED" && i.FieldKey == "periodStart" && i.TabNo == 1 && i.IsBlocking);
        Assert.Contains(result.Issues, i => i.Code == "ATTACHMENT_REQUIRED" && i.SectionKey == "section-2-1");
        Assert.Contains(result.Issues, i => i.Code == "FIELD_REQUIRED" && i.FieldKey == "summary");
    }

    [Fact]
    public void Checkbox_required_means_it_must_be_checked()
    {
        var unchecked_ = Run(Complete().Set("section-3-1", "proceduresCompleted", false));

        var issue = Assert.Single(For(unchecked_, "FIELD_REQUIRED"), i => i.FieldKey == "proceduresCompleted");
        Assert.Equal("Standard procedures completed must be confirmed.", issue.Message);
    }

    // ---- Tab 1 drives downstream behaviour -------------------------------------------------------------------

    [Fact]
    public void Tab_one_choices_decide_which_sections_exist()
    {
        var withDerivatives = Run(Complete().Config(equities: true, derivatives: true));
        var without = Run(Complete().Config(equities: true));

        Assert.True(withDerivatives.IsVisible("section-2-4"));
        Assert.True(withDerivatives.IsVisible("section-3-2"));
        Assert.False(without.IsVisible("section-2-4"));
        Assert.False(without.IsVisible("section-3-2"));
    }

    [Fact]
    public void Hiding_a_section_removes_its_issues_and_requirements_but_the_values_survive()
    {
        var scenario = Complete().Config(equities: true, derivatives: true);

        var shown = Run(scenario);
        Assert.Contains(shown.Issues, i => i.SectionKey == "section-3-2" && i.Code == "FIELD_REQUIRED");
        Assert.Contains(("section-3-2", "modelValidated"), shown.RequiredFields);

        scenario.Set("section-3-2", "notes", "kept while hidden").Config(equities: true); // untick Derivatives
        var hidden = Run(scenario);

        Assert.DoesNotContain(hidden.Issues, i => i.SectionKey is "section-3-2" or "section-2-4");
        Assert.DoesNotContain(("section-3-2", "modelValidated"), hidden.RequiredFields);

        var back = Run(scenario.Config(equities: true, derivatives: true));
        Assert.Contains(back.Issues, i => i.SectionKey == "section-3-2" && i.FieldKey == "modelValidated"); // required again
        Assert.DoesNotContain(back.Issues, i => i.SectionKey == "section-3-2" && i.FieldKey == "notes");     // restored value counts
    }

    [Fact]
    public void Attachment_requirement_follows_section_visibility_and_presence()
    {
        var scenario = Complete().Config(equities: true, legalReview: true).Set("section-3-3", "signOffDate", new DateOnly(2026, 2, 1));

        var missing = Run(scenario);
        Assert.Contains(missing.Issues, i => i.Code == "ATTACHMENT_REQUIRED" && i.SectionKey == "section-3-3");
        Assert.Contains("section-3-3", missing.AttachmentRequired);

        var present = Run(scenario.Attach(AttachmentScopeKeys.Section("section-3-3")));
        Assert.DoesNotContain(present.Issues, i => i.Code == "ATTACHMENT_REQUIRED");

        var notNeeded = Run(Complete()); // legal review off
        Assert.DoesNotContain("section-3-3", notNeeded.AttachmentRequired);
    }

    [Fact]
    public void Ledger_value_is_required_only_when_reconciliation_is_on()
    {
        var off = Run(Complete());
        var on = Run(Complete().Config(equities: true, reconciliation: true));

        Assert.DoesNotContain(("section-2-2", "ledgerValue"), off.RequiredFields);
        Assert.Contains(("section-2-2", "ledgerValue"), on.RequiredFields);
        Assert.Contains(on.Issues, i => i.Code == "FIELD_REQUIRED" && i.SectionKey == "section-2-2" && i.FieldKey == "ledgerValue");
    }

    // ---- Rule-driven messages ---------------------------------------------------------------------------------

    [Fact]
    public void Period_end_before_start_is_a_field_error()
    {
        var result = Run(Complete().Set("section-1-1", "periodEnd", new DateOnly(2024, 12, 31)));

        var issue = Assert.Single(For(result, "PERIOD_END_BEFORE_START"));
        Assert.Equal(IssueScope.Field, issue.Scope);
        Assert.Equal("periodEnd", issue.FieldKey);
        Assert.False(result.Summary.ReadyToSubmit);
    }

    [Fact]
    public void Short_summary_is_a_warning_that_does_not_block_submission()
    {
        var result = Run(Complete().Set("section-4-1", "summary", "Done."));

        var issue = Assert.Single(For(result, "SUMMARY_TOO_SHORT"));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.False(issue.IsBlocking);
        Assert.True(result.Summary.ReadyToSubmit);
        Assert.Equal(1, result.Summary.Counts.Warnings);
    }

    [Fact]
    public void Information_messages_do_not_block_either()
    {
        var result = Run(Complete().Set("section-2-1", "recordCount", null));

        Assert.Contains(result.Issues, i => i.Code == "RECORD_COUNT_MISSING" && i.Severity == IssueSeverity.Information);
        Assert.True(result.Summary.ReadyToSubmit);
        Assert.Equal(2, result.Summary.Counts.Infos); // record count + "reconciliation not in scope"
    }

    [Fact]
    public void Tab_scope_rules_target_a_tab_without_a_section()
    {
        var result = Run(Complete()); // reconciliation off

        var issue = Assert.Single(For(result, "RECONCILIATION_NOT_IN_SCOPE"));
        Assert.Equal(IssueScope.Tab, issue.Scope);
        Assert.Equal(5, issue.TabNo);
        Assert.Null(issue.SectionKey);
        Assert.Equal(1, result.CountsForTab(5).Infos);
    }

    [Fact]
    public void Too_long_text_is_an_error()
    {
        var result = Run(Complete().Set("section-2-1", "sourceSystem", new string('x', 201)));

        Assert.Contains(result.Issues, i => i.Code == "FIELD_TOO_LONG" && i.FieldKey == "sourceSystem");
    }

    // ---- Reconciliation ---------------------------------------------------------------------------------------

    private static Scenario Reconciled(decimal book, decimal? ledger)
        => Complete().Config(equities: true, reconciliation: true)
            .Set("section-2-2", "bookValue", book).Set("section-2-2", "ledgerValue", ledger);

    [Fact]
    public void A_significant_variance_blocks_readiness_and_requires_a_comment()
    {
        var result = Run(Reconciled(1_000_000m, 1_080_000m));

        var row = Assert.Single(result.Reconciliation);
        Assert.True(row.IsSignificant);
        Assert.Contains(result.Issues, i => i.Code == "RECON_VARIANCE_SIGNIFICANT" && i.Severity == IssueSeverity.Error && i.SectionKey == "section-5-1");
        Assert.Contains(result.Issues, i => i.Code == "SIGNIFICANT_VARIANCE_COMMENT" && i.FieldKey == "reconComment");
        Assert.Contains(result.Issues, i => i.Code == "VARIANCE_NOT_EXPLAINED");
        Assert.False(result.Summary.ReadyToSubmit);
    }

    [Fact]
    public void A_comment_clears_the_comment_requirement_but_not_the_variance_itself()
    {
        var result = Run(Reconciled(1_000_000m, 1_080_000m).Set("section-5-1", "reconComment", "Timing difference, agreed with finance."));

        Assert.DoesNotContain(result.Issues, i => i.Code == "SIGNIFICANT_VARIANCE_COMMENT");
        Assert.Contains(result.Issues, i => i.Code == "RECON_VARIANCE_SIGNIFICANT");
    }

    [Fact]
    public void A_warning_level_variance_is_a_warning_only()
    {
        var result = Run(Reconciled(1_000_000m, 1_020_000m).Set("section-4-2", "notes", "Explained."));

        Assert.Contains(result.Issues, i => i.Code == "RECON_VARIANCE_WARNING" && i.Severity == IssueSeverity.Warning);
        Assert.DoesNotContain(result.Issues, i => i.Code == "SIGNIFICANT_VARIANCE_COMMENT");
        Assert.DoesNotContain(result.Issues, i => i.Code == "VARIANCE_NOT_EXPLAINED");
        Assert.True(result.Summary.ReadyToSubmit);
    }

    [Fact]
    public void A_missing_ledger_value_is_surfaced_as_missing()
    {
        var result = Run(Reconciled(1_000_000m, null));

        Assert.True(Assert.Single(result.Reconciliation).IsMissing);
        Assert.Contains(result.Issues, i => i.Code == "RECON_VALUE_MISSING" && i.SectionKey == "section-5-1");
        Assert.False(result.Summary.ReadyToSubmit);
    }

    [Fact]
    public void Reconciliation_covers_only_visible_asset_classes_and_only_when_enabled()
    {
        var off = Run(Complete());
        var onTwo = Run(Reconciled(100m, 100m).Config(equities: true, fx: true, reconciliation: true)
            .Set("section-2-5", "bookValue", 50m).Set("section-2-5", "ledgerValue", 50m));

        Assert.Empty(off.Reconciliation);
        Assert.Equal(["equities", "fx"], onTwo.Reconciliation.Select(r => r.ComparisonKey));
        Assert.DoesNotContain(onTwo.Issues, i => i.Code.StartsWith("RECON_"));
    }

    [Fact]
    public void Hidden_section_values_do_not_feed_the_reconciliation()
    {
        var scenario = Reconciled(1_000_000m, 1_080_000m).Config(equities: false, fixedIncome: true, reconciliation: true)
            .Set("section-2-3", "bookValue", 10m).Set("section-2-3", "ledgerValue", 10m);

        var result = Run(scenario);

        Assert.Equal("fixedIncome", Assert.Single(result.Reconciliation).ComparisonKey);
    }

    // ---- Summary ---------------------------------------------------------------------------------------------

    [Fact]
    public void Issues_are_ordered_by_severity_then_position()
    {
        var result = Run(new Scenario().Config());

        var severities = result.Issues.Select(i => i.Severity).ToList();
        Assert.Equal(severities.OrderBy(s => s), severities);
        var errors = result.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.TabNo).ToList();
        Assert.Equal(errors.OrderBy(t => t), errors);
    }

    [Fact]
    public void Counts_per_tab_add_up_to_the_total()
    {
        var result = Run(new Scenario().Config());

        var perTab = Enumerable.Range(1, 5).Select(result.CountsForTab).ToList();
        var topLevel = result.Issues.Count(i => i.TabNo is null);

        Assert.Equal(result.Issues.Count, perTab.Sum(c => c.Errors + c.Warnings + c.Infos) + topLevel);
    }

    [Fact]
    public void Summary_hash_is_stable_and_changes_only_when_the_outcome_changes()
    {
        var first = Run(Complete()).Summary.Hash;
        var again = Run(Complete()).Summary.Hash;
        var valueChangeOnly = Run(Complete().Set("section-2-1", "sourceSystem", "A different source")).Summary.Hash;
        var outcomeChange = Run(Complete().Set("section-4-1", "summary", null)).Summary.Hash;

        Assert.Equal(first, again);
        Assert.Equal(first, valueChangeOnly);
        Assert.NotEqual(first, outcomeChange);
    }
}
