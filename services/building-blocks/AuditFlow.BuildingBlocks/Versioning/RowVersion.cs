using AuditFlow.BuildingBlocks.Errors;

namespace AuditFlow.BuildingBlocks.Versioning;

/// <summary>SQL Server rowversion exposed to clients as an opaque base64 token (ETag / If-Match / SSE "version").</summary>
public static class RowVersion
{
    public static string ToToken(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static byte[] FromToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ValidationFailedException("version", "A version (If-Match) is required.");
        try
        {
            return Convert.FromBase64String(token.Trim('"'));
        }
        catch (FormatException)
        {
            throw new ValidationFailedException("version", "The version token is malformed.");
        }
    }

    public static string ToETag(string token) => $"\"{token}\"";
}
