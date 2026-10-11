using System.Globalization;
using System.Text.Json;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>Named values rules can read: config facts, period/region, "section.field" values, computed "recon.*" flags.</summary>
public sealed class Facts(IDictionary<string, object?> values)
{
    public static Facts From(IDictionary<string, object?> values) => new(values);

    public object? Get(string? name) => name is not null && values.TryGetValue(name, out var v) ? v : null;

    public void Set(string name, object? value) => values[name] = value;

    public bool Contains(string name) => values.ContainsKey(name);
}

/// <summary>Pure condition evaluation and the section visibility derived from Tab 1 facts.</summary>
public interface IRuleEvaluator
{
    /// <summary>A missing condition always holds.</summary>
    bool Holds(Condition? condition, Facts facts);

    IReadOnlyDictionary<string, bool> ResolveVisibility(RuleSet ruleSet, Facts baseFacts);
}

public sealed class RuleEvaluator : IRuleEvaluator
{
    public static readonly IReadOnlySet<string> KnownOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "equals", "in", "isTrue", "isEmpty", "gt", "gte", "lt", "lte", "lengthLt", "not", "all", "any"
    };

    public bool Holds(Condition? condition, Facts facts)
    {
        if (condition is null) return true;

        switch (condition.Op.ToLowerInvariant())
        {
            case "all":
                return (condition.Conditions ?? []).All(c => Holds(c, facts));
            case "any":
                return (condition.Conditions ?? []).Any(c => Holds(c, facts));
            case "not":
                return !Holds(condition.Inner, facts);
            case "equals":
                return FieldValues.AreEqual(facts.Get(condition.Fact), Operand(condition, facts));
            case "in":
                var actual = facts.Get(condition.Fact);
                return (condition.Values ?? []).Any(v => FieldValues.AreEqual(actual, Literal(v)));
            case "istrue":
                return facts.Get(condition.Fact) is true;
            case "isempty":
                return FieldValues.IsEmpty(facts.Get(condition.Fact));
            case "gt": return Compare(facts.Get(condition.Fact), Operand(condition, facts)) is > 0;
            case "gte": return Compare(facts.Get(condition.Fact), Operand(condition, facts)) is >= 0;
            case "lt": return Compare(facts.Get(condition.Fact), Operand(condition, facts)) is < 0;
            case "lte": return Compare(facts.Get(condition.Fact), Operand(condition, facts)) is <= 0;
            case "lengthlt":
                return facts.Get(condition.Fact) is string s && Literal(condition.Value) is decimal max && s.Length < max;
            default:
                throw new InvalidOperationException($"Unknown rule operator '{condition.Op}'.");
        }
    }

    public IReadOnlyDictionary<string, bool> ResolveVisibility(RuleSet ruleSet, Facts baseFacts)
        => ruleSet.Sections.ToDictionary(s => s.Key, s => Holds(s.VisibleWhen, baseFacts));

    private object? Operand(Condition condition, Facts facts)
        => condition.Other is not null ? facts.Get(condition.Other) : Literal(condition.Value);

    private static object? Literal(JsonElement? json) => json is { } value ? Literal(value) : null;

    private static object? Literal(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => json.GetString(),
        JsonValueKind.Number => json.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    /// <summary>Orders numbers with numbers and dates with dates (a date may be compared with a yyyy-MM-dd literal). Otherwise null.</summary>
    private static int? Compare(object? a, object? b)
    {
        if (a is null || b is null) return null;
        if (a is decimal x && b is decimal y) return x.CompareTo(y);

        var dateA = AsDate(a);
        var dateB = AsDate(b);
        return dateA is { } da && dateB is { } db ? da.CompareTo(db) : null;
    }

    private static DateOnly? AsDate(object value) => value switch
    {
        DateOnly d => d,
        string s when DateOnly.TryParseExact(s, FieldValues.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null
    };
}
