using System.Text.Json;
using AuditFlow.Attachment.Api.Endpoints;
using AuditFlow.Auth.Api.Endpoints;
using AuditFlow.Engagement.Api.Endpoints;
using AuditFlow.Notification.Api.Endpoints;
using AuditFlow.Review.Api.Endpoints;
using AuditFlow.Workflow.Api.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AuditFlow.Security.Tests;

/// <summary>
/// Every route a service maps must require authorization unless it is on the explicit allow-list below. The list is the
/// contract: adding an anonymous route means editing it here, in review. Routes mapped by UseAuditFlowDefaults ("/",
/// "/health*", Swagger) are infrastructure and intentionally outside this inventory.
/// </summary>
public class RouteInventoryTests
{
    /// <summary>service -> anonymous routes it is allowed to expose.</summary>
    private static readonly Dictionary<string, string[]> AllowAnonymous = new()
    {
        ["auth"] = ["POST /auth/login", "GET /.well-known/openid-configuration", "GET /.well-known/jwks.json"],
        ["engagement"] = [],
        ["workflow"] = [],
        ["attachment"] = [],
        ["review"] = [],
        ["notification"] = [],
    };

    private static readonly Dictionary<string, Action<WebApplication>> Mappers = new()
    {
        ["auth"] = app => app.MapAuthEndpoints(),
        ["engagement"] = app => app.MapEngagementEndpoints(),
        ["workflow"] = app => app.MapWorkflowEndpoints(),
        ["attachment"] = app => app.MapAttachmentEndpoints(),
        ["review"] = app => app.MapReviewEndpoints(),
        ["notification"] = app => app.MapStreamEndpoints(),
    };

    public static TheoryData<string> Services
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var service in Mappers.Keys) data.Add(service);
            return data;
        }
    }

    /// <summary>
    /// Endpoint construction decides per handler parameter whether it is a service or a request body. The inventory never runs
    /// handlers, so treating every parameter as a service keeps the real DI registrations out of the test.
    /// </summary>
    private sealed class EverythingIsAService : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => true;
    }

    // The container answers IServiceProviderIsService itself, so a registration is ignored; wrap the provider instead.
    private sealed class EverythingIsAServiceFactory : IServiceProviderFactory<IServiceCollection>
    {
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;
        public IServiceProvider CreateServiceProvider(IServiceCollection services) => new Wrapper(services.BuildServiceProvider());
    }

    private sealed class Wrapper(ServiceProvider inner) : IServiceProvider, IDisposable, IAsyncDisposable
    {
        private static readonly IServiceProviderIsService IsService = new EverythingIsAService();

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? IsService : inner.GetService(serviceType);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed record Route(string Method, string Pattern, bool Authorized, bool Anonymous)
    {
        public string Key => $"{Method} {Pattern}";
    }

    /// <summary>Maps the service's endpoints onto a host that is built but never started: no SQL Server, no ports.</summary>
    private static List<Route> Inventory(string service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseServiceProviderFactory(new EverythingIsAServiceFactory());
        var app = builder.Build();
        Mappers[service](app);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();
                var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                return methods.Select(method => new Route(method, "/" + endpoint.RoutePattern.RawText!.TrimStart('/'), authorized, anonymous));
            })
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Service_maps_routes(string service)
        => Assert.NotEmpty(Inventory(service)); // guards against a vacuous pass if mapping silently stops working

    [Theory]
    [MemberData(nameof(Services))]
    public void Every_route_requires_authorization_unless_explicitly_allowed(string service)
    {
        var allowed = AllowAnonymous[service];

        var unprotected = Inventory(service)
            .Where(r => (!r.Authorized || r.Anonymous) && !allowed.Contains(r.Key))
            .Select(r => r.Key)
            .ToList();

        Assert.True(unprotected.Count == 0, $"{service}-service has routes without authorization: {string.Join(", ", unprotected)}");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Allow_list_has_no_stale_entries(string service)
    {
        var mapped = Inventory(service).Select(r => r.Key).ToHashSet();

        var stale = AllowAnonymous[service].Where(entry => !mapped.Contains(entry)).ToList();

        Assert.True(stale.Count == 0, $"Allow-list entries that no longer exist in {service}-service: {string.Join(", ", stale)}");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Internal_routes_always_require_authorization(string service)
    {
        var open = Inventory(service)
            .Where(r => r.Pattern.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase) && (!r.Authorized || r.Anonymous))
            .Select(r => r.Key);

        Assert.Empty(open);
    }

    [Fact]
    public void Gateway_does_not_route_internal_paths()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gateway.appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var routes = document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject()
            .Select(route => (route.Name, Path: route.Value.GetProperty("Match").GetProperty("Path").GetString()!))
            .ToList();

        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, route => route.Path.Contains("internal", StringComparison.OrdinalIgnoreCase));
    }
}
