using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

public sealed record ReconciliationInput(ReconciliationPair Pair, decimal? Expected, decimal? Actual);

/// <summary>
/// Generalized variance indicators (Open Decision 1): expected = book value, actual = ledger value.
/// The indicator is the worst of the percentage level and the absolute value level. Not production financial maths.
/// </summary>
public interface IReconciliationCalculator
{
    IReadOnlyList<ReconciliationRowDto> Calculate(ReconciliationThresholds thresholds, IEnumerable<ReconciliationInput> inputs);
}

public sealed class ReconciliationCalculator : IReconciliationCalculator
{
    public IReadOnlyList<ReconciliationRowDto> Calculate(ReconciliationThresholds thresholds, IEnumerable<ReconciliationInput> inputs)
        => inputs.Select(i => Row(thresholds, i)).ToList();

    private static ReconciliationRowDto Row(ReconciliationThresholds thresholds, ReconciliationInput input)
    {
        var pair = input.Pair;
        if (input.Expected is not { } expected || input.Actual is not { } actual)
        {
            return new ReconciliationRowDto(pair.Key, pair.Label, input.Expected, input.Actual, null, null,
                VarianceIndicator.Missing, IsSignificant: false, IsMissing: true);
        }

        var valueVariance = actual - expected;

        // Zero expected with a non-zero actual has no meaningful percentage: always significant.
        decimal? percent = expected != 0 ? Math.Abs(valueVariance) / Math.Abs(expected) * 100m : (actual == 0 ? 0m : null);

        var valueLevel = Level(Math.Abs(valueVariance), thresholds.Value);
        var percentLevel = percent is { } p ? Level(p, thresholds.Percent) : VarianceIndicator.Significant;
        var indicator = (VarianceIndicator)Math.Max((int)valueLevel, (int)percentLevel);

        return new ReconciliationRowDto(pair.Key, pair.Label, expected, actual, valueVariance,
            percent is { } rounded ? Math.Round(rounded, 2) : null,
            indicator, indicator == VarianceIndicator.Significant, IsMissing: false);
    }

    // Ok < warning <= Warning <= significant < Significant. Enum order Ok, Warning, Significant allows Math.Max.
    private static VarianceIndicator Level(decimal magnitude, ThresholdLevels levels)
        => magnitude > levels.Significant ? VarianceIndicator.Significant
         : magnitude >= levels.Warning ? VarianceIndicator.Warning
         : VarianceIndicator.Ok;
}
