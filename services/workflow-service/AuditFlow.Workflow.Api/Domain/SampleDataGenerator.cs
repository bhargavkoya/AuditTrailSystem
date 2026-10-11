using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>
/// Demo data for engagements the seeder created in a later lifecycle status, so every seeded engagement looks like
/// real work in progress: Draft has Tab 1 only, In Progress adds Tab 2, later statuses are complete.
/// Driven by the rule set (types, visibility, reconciliation pairs), never by hardcoded sections.
/// </summary>
public static class SampleDataGenerator
{
    private const decimal BaseBookValue = 1_000_000m;

    public static void Fill(
        IDictionary<string, Dictionary<string, object?>> values, RuleSet ruleSet, EngagementStatus status,
        string periodType, string region, string ownerName, DateTimeOffset now, IRuleEvaluator evaluator)
    {
        var (start, end) = Period(periodType, now);
        var context = new EvaluationContext(periodType, region,
            values.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)kv.Value), new HashSet<string>());
        var visibility = evaluator.ResolveVisibility(ruleSet, ValidationEngine.BuildBaseFacts(ruleSet, context));

        var maxTab = status switch
        {
            EngagementStatus.Draft => 1,
            EngagementStatus.InProgress => 2,
            _ => 5
        };
        // A rejected engagement carries a warning-level variance; the others reconcile cleanly.
        var ledgerFactor = status == EngagementStatus.Rejected ? 1.03m : 1.002m;
        var pairs = ruleSet.Reconciliation.Pairs.ToList();

        foreach (var section in ruleSet.Sections.Where(s => s.Tab <= maxTab && visibility[s.Key]))
        {
            var pairIndex = pairs.FindIndex(p => p.Section == section.Key);
            var pair = pairIndex >= 0 ? pairs[pairIndex] : null;

            foreach (var field in section.Fields)
            {
                if (field.Source is not null) continue; // Tab 1 choices come from the creation request

                values[section.Key][field.Key] = field.Type switch
                {
                    FieldTypes.Checkbox => true,
                    FieldTypes.Date => field.Key == "periodStart" ? start : field.Key == "periodEnd" ? end : end.AddDays(15),
                    FieldTypes.Number when pair is not null && field.Key == pair.Expected => BaseBookValue * (pairIndex + 1),
                    FieldTypes.Number when pair is not null && field.Key == pair.Actual => Math.Round(BaseBookValue * (pairIndex + 1) * ledgerFactor, 2),
                    FieldTypes.Number => 12_450m,
                    FieldTypes.Documentation => $"Sample entry for '{field.Label}' generated for the demo data set; replace it with the audit team's own notes.",
                    _ => field.Key == "performedBy" ? ownerName
                        : field.Key == "sourceSystem" ? "Murex general ledger extract"
                        : $"Sample {field.Label.ToLowerInvariant()}"
                };
            }
        }
    }

    /// <summary>The most recent completed period of the given type.</summary>
    public static (DateOnly Start, DateOnly End) Period(string periodType, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var months = periodType switch { "Quarterly" => 3, "Half-Yearly" => 6, _ => 12 };
        var currentStart = new DateOnly(today.Year, (today.Month - 1) / months * months + 1, 1);
        var start = currentStart.AddMonths(-months);
        return (start, currentStart.AddDays(-1));
    }
}
