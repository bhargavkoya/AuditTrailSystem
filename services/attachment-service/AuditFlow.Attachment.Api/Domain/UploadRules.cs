using System.Text;
using AuditFlow.BuildingBlocks.Errors;

namespace AuditFlow.Attachment.Api.Domain;

/// <summary>Pure upload rules: size limit, file name sanitizing, content type handling, scope shape.</summary>
public static class UploadRules
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxFileNameLength = 200;
    public const int MaxContentTypeLength = 100;
    public const int MinTab = 1;
    public const int MaxTab = 5;

    /// <summary>
    /// Keeps only the final path segment, drops control and path-hostile characters, trims dots and spaces,
    /// and caps the length while preserving the extension. The name is metadata only; it never touches the file system.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        var lastSegment = (name ?? string.Empty).Split('/', '\\')[^1];
        var invalid = new HashSet<char>("<>:\"|?*") ;
        var cleaned = new StringBuilder();
        foreach (var c in lastSegment)
            if (!char.IsControl(c) && !invalid.Contains(c)) cleaned.Append(c);

        var result = cleaned.ToString().Trim().Trim('.').Trim();
        if (result.Length == 0) return "file";
        if (result.Length <= MaxFileNameLength) return result;

        var extension = Path.GetExtension(result);
        if (extension.Length > 20) extension = string.Empty;
        return result[..(MaxFileNameLength - extension.Length)] + extension;
    }

    /// <summary>Recorded for display only; it is never trusted when serving the file.</summary>
    public static string NormalizeContentType(string? contentType)
    {
        var value = (contentType ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > MaxContentTypeLength || value.Any(char.IsControl)) return "application/octet-stream";
        return value;
    }

    public static void ValidateSize(long length)
    {
        if (length <= 0) throw new ValidationFailedException("file", "The file is empty.");
        if (length > MaxBytes) throw new ValidationFailedException("file", $"The file exceeds the {MaxBytes / (1024 * 1024)} MB limit.");
    }

    public static void ValidateTab(int tabNo)
    {
        if (tabNo is < MinTab or > MaxTab) throw new ValidationFailedException("tabNo", $"Tab must be between {MinTab} and {MaxTab}.");
    }
}
