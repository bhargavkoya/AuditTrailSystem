using System.Text.Json;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Tests;

public class RuleEvaluatorTests
{
    private static readonly RuleEvaluator Evaluator = new();

    private static Condition Parse(string json) => JsonSerializer.Deserialize<Condition>(json, RuleSetJson.Options)!;

    private static Facts F(params (string Key, object? Value)[] values)
        => Facts.From(values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Missing_condition_always_holds() => Assert.True(Evaluator.Holds(null, F()));

    [Theory]
    [InlineData("""{"op":"isTrue","fact":"a"}""", true, true)]
    [InlineData("""{"op":"isTrue","fact":"a"}""", false, false)]
    [InlineData("""{"op":"isTrue","fact":"missing"}""", true, false)]
    public void IsTrue(string json, bool fact, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse(json), F(("a", fact))));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("x", false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsEmpty(object? value, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse("""{"op":"isEmpty","fact":"a"}"""), F(("a", value))));

    [Theory]
    [InlineData("Annual", true)]
    [InlineData("annual", true)]
    [InlineData("Quarterly", false)]
    public void Equals_is_case_insensitive_for_text(string actual, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse("""{"op":"equals","fact":"periodType","value":"Annual"}"""), F(("periodType", actual))));

    [Fact]
    public void Equals_compares_numbers_and_against_other_facts()
    {
        Assert.True(Evaluator.Holds(Parse("""{"op":"equals","fact":"n","value":5}"""), F(("n", 5m))));
        Assert.True(Evaluator.Holds(Parse("""{"op":"equals","fact":"a","other":"b"}"""), F(("a", 3m), ("b", 3m))));
        Assert.False(Evaluator.Holds(Parse("""{"op":"equals","fact":"a","value":"x"}"""), F()));
    }

    [Theory]
    [InlineData("EMEA", true)]
    [InlineData("APAC", true)]
    [InlineData("AMER", false)]
    public void In_matches_any_listed_value(string region, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse("""{"op":"in","fact":"region","values":["EMEA","APAC"]}"""), F(("region", region))));

    [Theory]
    [InlineData("gt", 6, true)]
    [InlineData("gt", 5, false)]
    [InlineData("gte", 5, true)]
    [InlineData("lt", 4, true)]
    [InlineData("lt", 5, false)]
    [InlineData("lte", 5, true)]
    public void Numeric_comparisons(string op, int actual, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse($$"""{"op":"{{op}}","fact":"n","value":5}"""), F(("n", (decimal)actual))));

    [Fact]
    public void Dates_compare_with_dates_and_with_iso_literals()
    {
        var facts = F(("end", new DateOnly(2025, 1, 1)), ("start", new DateOnly(2025, 3, 1)));

        Assert.True(Evaluator.Holds(Parse("""{"op":"lt","fact":"end","other":"start"}"""), facts));
        Assert.False(Evaluator.Holds(Parse("""{"op":"gt","fact":"end","other":"start"}"""), facts));
        Assert.True(Evaluator.Holds(Parse("""{"op":"gte","fact":"end","value":"2025-01-01"}"""), facts));
    }

    [Fact]
    public void Comparison_with_a_missing_or_incompatible_value_is_false()
    {
        Assert.False(Evaluator.Holds(Parse("""{"op":"lt","fact":"a","value":5}"""), F()));
        Assert.False(Evaluator.Holds(Parse("""{"op":"gt","fact":"a","value":5}"""), F(("a", "text"))));
        Assert.False(Evaluator.Holds(Parse("""{"op":"lt","fact":"a","other":"b"}"""), F(("a", 1m), ("b", new DateOnly(2025, 1, 1)))));
    }

    [Theory]
    [InlineData("short", true)]
    [InlineData("exactly ten", false)]
    [InlineData("a longer piece of text", false)]
    [InlineData(null, false)]
    public void LengthLt_needs_a_text_value(string? text, bool expected)
        => Assert.Equal(expected, Evaluator.Holds(Parse("""{"op":"lengthLt","fact":"a","value":11}"""), F(("a", text))));

    [Fact]
    public void All_any_and_not_nest()
    {
        var condition = Parse("""
            {"op":"all","conditions":[
              {"op":"isTrue","fact":"a"},
              {"op":"any","conditions":[{"op":"isTrue","fact":"b"},{"op":"not","condition":{"op":"isTrue","fact":"c"}}]}]}
            """);

        Assert.True(Evaluator.Holds(condition, F(("a", true), ("b", false), ("c", false))));   // not c
        Assert.True(Evaluator.Holds(condition, F(("a", true), ("b", true), ("c", true))));     // b
        Assert.False(Evaluator.Holds(condition, F(("a", true), ("b", false), ("c", true))));   // neither
        Assert.False(Evaluator.Holds(condition, F(("a", false), ("b", true), ("c", false))));  // a fails
    }

    [Fact]
    public void Empty_all_holds_and_empty_any_does_not()
    {
        Assert.True(Evaluator.Holds(Parse("""{"op":"all","conditions":[]}"""), F()));
        Assert.False(Evaluator.Holds(Parse("""{"op":"any","conditions":[]}"""), F()));
    }

    [Fact]
    public void Unknown_operator_throws() =>
        Assert.Throws<InvalidOperationException>(() => Evaluator.Holds(Parse("""{"op":"bogus","fact":"a"}"""), F()));

    [Fact]
    public void Visibility_follows_the_conditions_on_base_facts()
    {
        var ruleSet = RuleSetJson.LoadEmbedded();

        var visibility = Evaluator.ResolveVisibility(ruleSet, F(
            ("cfg.equities", true), ("cfg.fixedIncome", false), ("cfg.derivatives", true), ("cfg.fx", false),
            ("opt.reconciliation", false), ("opt.legalReview", true)));

        Assert.True(visibility["section-1-1"]);
        Assert.True(visibility["section-2-2"]);
        Assert.False(visibility["section-2-3"]);
        Assert.True(visibility["section-2-4"]);
        Assert.False(visibility["section-2-5"]);
        Assert.True(visibility["section-3-2"]);
        Assert.True(visibility["section-3-3"]);
        Assert.False(visibility["section-5-1"]);
        Assert.True(visibility["section-5-2"]);
    }
}
