using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuditFlow.BuildingBlocks.Hosting;

/// <summary>
/// Fail-fast configuration. Local development keeps localhost fallbacks so <c>dotnet run</c> just works; any other
/// environment must configure the value explicitly, otherwise the service refuses to start and names the key.
/// </summary>
public static class StartupConfig
{
    public static string Require(IConfiguration configuration, IHostEnvironment environment, string key, string? developmentDefault = null)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (environment.IsDevelopment() && developmentDefault is not null) return developmentDefault;
            throw new InvalidOperationException(
                $"Configuration '{key}' is required in the {environment.EnvironmentName} environment (env var '{key.Replace(":", "__")}').");
        }

        return value;
    }

    /// <summary>A required absolute URL, e.g. <c>Services:Workflow:BaseUrl</c>.</summary>
    public static Uri RequireUrl(IConfiguration configuration, IHostEnvironment environment, string key, string? developmentDefault = null)
    {
        var value = Require(configuration, environment, key, developmentDefault);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"Configuration '{key}' must be an absolute http(s) URL but was '{value}'.");
    }

    public static Uri ServiceUrl(this WebApplicationBuilder builder, string service, string developmentDefault)
        => RequireUrl(builder.Configuration, builder.Environment, $"Services:{service}:BaseUrl", developmentDefault);
}
