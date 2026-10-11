using System.Text.Json;
using AuditFlow.Contracts.Api;
using AuditFlow.Workflow.Api.Domain;

namespace AuditFlow.Workflow.Tests;

public class ReconciliationCalculatorTests
{
    private static readonly ReconciliationThresholds Thresholds = new(new ThresholdLevels(1, 5), new ThresholdLevels(10_000, 100_000));
    private static readonly ReconciliationPair Pair = new("equities", "Equities", "section-2-2", "bookValue", "ledgerValue");

    private static ReconciliationRowDto Row(decimal? expected, decimal? actual)
        => new ReconciliationCalculator().Calculate(Thresholds, [new ReconciliationInput(Pair, expected, actual)]).Single();

    [Theory]
    [InlineData(1_000_000, 1_000_000, VarianceIndicator.Ok)]
    [InlineData(1_000_000, 1_005_000, VarianceIndicator.Ok)]            // 0.5% and 5,000
    [InlineData(1_000_000, 1_010_000, VarianceIndicator.Warning)]       // exactly 1% and 10,000
    [InlineData(1_000_000, 1_050_000, VarianceIndicator.Warning)]       // exactly 5% => still Warning
    [InlineData(1_000_000, 1_050_001, VarianceIndicator.Significant)]   // just over 5%
    [InlineData(1_000_000, 1_080_000, VarianceIndicator.Significant)]   // 8%
    [InlineData(1_000_000, 920_000, VarianceIndicator.Significant)]     // negative variance counts by magnitude
    public void Percentage_levels(decimal expected, decimal actual, VarianceIndicator indicator)
        => Assert.Equal(indicator, Row(expected, actual).Indicator);

    [Fact]
    public void Small_percentage_but_large_absolute_value_takes_the_worse_level()
    {
        // 0.5% of 100 million is 500,000 => value level Significant even though the percentage is Ok.
        var row = Row(100_000_000m, 100_500_000m);

        Assert.Equal(VarianceIndicator.Significant, row.Indicator);
        Assert.True(row.IsSignificant);
        Assert.Equal(0.5m, row.PercentVariance);
    }

    [Fact]
    public void Large_percentage_but_small_absolute_value_takes_the_worse_level()
    {
        var row = Row(1_000m, 1_100m); // 10%, 100

        Assert.Equal(VarianceIndicator.Significant, row.Indicator);
    }

    [Fact]
    public void Variances_and_rounding()
    {
        var row = Row(300m, 310m);

        Assert.Equal(10m, row.ValueVariance);
        Assert.Equal(3.33m, row.PercentVariance);
        Assert.Equal(VarianceIndicator.Warning, row.Indicator);
    }

    [Fact]
    public void Zero_expected_with_a_non_zero_actual_is_significant_without_a_percentage()
    {
        var row = Row(0m, 5m);

        Assert.Equal(VarianceIndicator.Significant, row.Indicator);
        Assert.Null(row.PercentVariance);
        Assert.Equal(5m, row.ValueVariance);
    }

    [Fact]
    public void Zero_on_both_sides_is_ok()
    {
        var row = Row(0m, 0m);

        Assert.Equal(VarianceIndicator.Ok, row.Indicator);
        Assert.Equal(0m, row.PercentVariance);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(10, null)]
    [InlineData(null, null)]
    public void Missing_values_are_flagged_and_not_compared(int? expected, int? actual)
    {
        var row = Row(expected, actual);

        Assert.True(row.IsMissing);
        Assert.False(row.IsSignificant);
        Assert.Equal(VarianceIndicator.Missing, row.Indicator);
        Assert.Null(row.ValueVariance);
        Assert.Null(row.PercentVariance);
    }
}

public class FieldValuesTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static FieldDefinition Field(string type, int? maxLength = null) => new("f", type, "Field", MaxLength: maxLength);

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void Checkbox_accepts_booleans(string json, bool expected)
        => Assert.Equal(expected, FieldValues.Parse(Field(FieldTypes.Checkbox), J(json)).Value);

    [Fact]
    public void Checkbox_rejects_other_types() => Assert.False(FieldValues.Parse(Field(FieldTypes.Checkbox), J("\"yes\"")).IsValid);

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("\"1234.50\"", 1234.50)]
    [InlineData("-3", -3)]
    public void Number_accepts_numbers_and_numeric_text(string json, double expected)
        => Assert.Equal((decimal)expected, FieldValues.Parse(Field(FieldTypes.Number), J(json)).Value);

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Number_rejects_non_numbers(string json) => Assert.False(FieldValues.Parse(Field(FieldTypes.Number), J(json)).IsValid);

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    public void Blank_values_clear_non_checkbox_fields(string json)
    {
        foreach (var type in new[] { FieldTypes.Number, FieldTypes.Date, FieldTypes.Text, FieldTypes.Documentation })
        {
            var parsed = FieldValues.Parse(Field(type), J(json));
            Assert.True(parsed.IsValid);
            Assert.Null(parsed.Value);
        }
    }

    [Fact]
    public void Date_requires_iso_format()
    {
        Assert.Equal(new DateOnly(2025, 3, 31), FieldValues.Parse(Field(FieldTypes.Date), J("\"2025-03-31\"")).Value);
        Assert.False(FieldValues.Parse(Field(FieldTypes.Date), J("\"31/03/2025\"")).IsValid);
        Assert.False(FieldValues.Parse(Field(FieldTypes.Date), J("\"2025-02-30\"")).IsValid);
        Assert.False(FieldValues.Parse(Field(FieldTypes.Date), J("20250331")).IsValid);
    }

    [Fact]
    public void Text_enforces_max_length()
    {
        Assert.True(FieldValues.Parse(Field(FieldTypes.Text, 3), J("\"abc\"")).IsValid);
        Assert.False(FieldValues.Parse(Field(FieldTypes.Text, 3), J("\"abcd\"")).IsValid);
        Assert.False(FieldValues.Parse(Field(FieldTypes.Text), J("5")).IsValid);
    }

    [Fact]
    public void Unknown_field_type_is_rejected() => Assert.False(FieldValues.Parse(Field("blob"), J("1")).IsValid);

    [Theory]
    [InlineData(FieldTypes.Checkbox, true)]
    [InlineData(FieldTypes.Text, "hello")]
    [InlineData(FieldTypes.Documentation, "notes")]
    [InlineData(FieldTypes.Number, 1234.5)]
    [InlineData(FieldTypes.Date, null)]
    public void Storage_round_trips(string type, object? raw)
    {
        object? value = raw is double d ? (decimal)d : raw;
        if (type == FieldTypes.Date) value = new DateOnly(2025, 6, 30);

        var stored = FieldValues.ToStored(type, value);

        Assert.Equal(value, FieldValues.FromStored(type, stored));
    }

    [Fact]
    public void Empty_values_round_trip_as_null_and_unchecked()
    {
        Assert.Null(FieldValues.FromStored(FieldTypes.Number, FieldValues.ToStored(FieldTypes.Number, null)));
        Assert.Null(FieldValues.FromStored(FieldTypes.Text, StoredValue.Empty));
        Assert.Equal(false, FieldValues.FromStored(FieldTypes.Checkbox, StoredValue.Empty));
    }

    [Fact]
    public void View_values_render_dates_as_iso_strings()
    {
        Assert.Equal("2025-06-30", FieldValues.ToView(new DateOnly(2025, 6, 30)));
        Assert.Equal(5m, FieldValues.ToView(5m));
    }
}
