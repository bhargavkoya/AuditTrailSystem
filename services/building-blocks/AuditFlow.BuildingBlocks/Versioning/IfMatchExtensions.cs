using AuditFlow.BuildingBlocks.Errors;
using Microsoft.AspNetCore.Http;

namespace AuditFlow.BuildingBlocks.Versioning;

public static class IfMatchExtensions
{
    /// <summary>
    /// Reads the If-Match header as an opaque version token. Missing or malformed => 422 (ValidationFailedException).
    /// A stale token is detected by the data layer and surfaces as 409 (ConcurrencyException).
    /// </summary>
    public static string GetIfMatchToken(this HttpRequest request)
    {
        var raw = request.Headers.IfMatch.ToString();
        _ = RowVersion.FromToken(raw); // validates presence and format
        return raw.Trim().Trim('"');
    }

    public static byte[] GetIfMatchVersion(this HttpRequest request) => RowVersion.FromToken(request.Headers.IfMatch.ToString());
}
