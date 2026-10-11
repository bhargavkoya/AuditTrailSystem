using System.Globalization;
using System.Text.Json;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Workflow.Api.Domain;

/// <summary>A field value as stored: exactly one of the columns is used, chosen by the field type.</summary>
public readonly record struct StoredValue(string? Text, bool? Bool, DateOnly? Date)
{
    public static readonly StoredValue Empty = new(null, null, null);
}

public sealed record ParsedValue(object? Value, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Converts between the wire (JSON), the domain (bool / string / decimal / DateOnly / null) and storage.
/// Numbers are kept as invariant text so no precision is lost.
/// </summary>
public static class FieldValues
{
    public const string DateFormat = "yyyy-MM-dd";

    public static ParsedValue Parse(FieldDefinition field, JsonElement json)
    {
        try
        {
            return field.Type switch
            {
                FieldTypes.Checkbox => ParseBool(json),
                FieldTypes.Number => ParseNumber(json),
                FieldTypes.Date => ParseDate(json),
                FieldTypes.Text or FieldTypes.Documentation => ParseText(field, json),
                _ => new ParsedValue(null, $"Unsupported field type '{field.Type}'.")
            };
        }
        catch (InvalidOperationException)
        {
            return new ParsedValue(null, "The value has the wrong type.");
        }
    }

    private static ParsedValue ParseBool(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.True => new(true, null),
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => new(false, null),
        _ => new(null, "Expected true or false.")
    };

    private static ParsedValue ParseText(FieldDefinition field, JsonElement json)
    {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(null, null);
        if (json.ValueKind != JsonValueKind.String) return new(null, "Expected text.");

        var text = json.GetString()!;
        if (string.IsNullOrWhiteSpace(text)) return new(null, null);
        return field.MaxLength is { } max && text.Length > max
            ? new(null, $"Must be at most {max} characters.")
            : new(text, null);
    }

    private static ParsedValue ParseNumber(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return new(null, null);
            case JsonValueKind.Number when json.TryGetDecimal(out var n):
                return new(n, null);
            case JsonValueKind.String:
                var text = json.GetString();
                if (string.IsNullOrWhiteSpace(text)) return new(null, null);
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? new(parsed, null)
                    : new(null, "Enter a valid number.");
            default:
                return new(null, "Enter a valid number.");
        }
    }

    private static ParsedValue ParseDate(JsonElement json)
    {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(null, null);
        if (json.ValueKind != JsonValueKind.String) return new(null, "Enter a date as yyyy-MM-dd.");

        var text = json.GetString();
        if (string.IsNullOrWhiteSpace(text)) return new(null, null);
        return DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new(date, null)
            : new(null, "Enter a date as yyyy-MM-dd.");
    }

    public static StoredValue ToStored(string fieldType, object? value) => fieldType switch
    {
        FieldTypes.Checkbox => new(null, value as bool? ?? false, null),
        FieldTypes.Date => new(null, null, value as DateOnly?),
        FieldTypes.Number => new(value is decimal d ? d.ToString(CultureInfo.InvariantCulture) : null, null, null),
        _ => new(value as string, null, null)
    };

    public static object? FromStored(string fieldType, StoredValue stored) => fieldType switch
    {
        FieldTypes.Checkbox => stored.Bool ?? false,
        FieldTypes.Date => stored.Date,
        FieldTypes.Number => decimal.TryParse(stored.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => stored.Text
    };

    /// <summary>JSON-friendly value for the API: dates as yyyy-MM-dd strings.</summary>
    public static object? ToView(object? value) => value is DateOnly date ? date.ToString(DateFormat, CultureInfo.InvariantCulture) : value;

    public static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        bool b => !b,
        _ => false
    };

    public static bool AreEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase),
        _ => Equals(a, b)
    };
}
