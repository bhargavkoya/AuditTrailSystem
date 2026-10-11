using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.BuildingBlocks.Hosting;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using AuditFlow.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuditFlow.BuildingBlocks.Access;

/// <summary>
/// Asks engagement-service (the only permission authority, ADR 0004) what the caller may do on an engagement.
/// Answers are cached briefly and evicted when the engagement's status changes.
/// </summary>
public interface IEngagementAccessClient
{
    /// <summary>403 => ForbiddenException, 404 => NotFoundException, other failures => HttpRequestException (502).</summary>
    Task<EngagementAccessDto> GetAccessAsync(string engagementId, CancellationToken cancellationToken);
}

public static class EngagementAccessClientExtensions
{
    /// <summary>Returns the access answer or throws ForbiddenException when the caller lacks the capability.</summary>
    public static async Task<EngagementAccessDto> RequireAsync(
        this IEngagementAccessClient client, string engagementId, string capability, CancellationToken cancellationToken)
    {
        var access = await client.GetAccessAsync(engagementId, cancellationToken);
        return access.Capabilities.Contains(capability)
            ? access
            : throw new ForbiddenException("You are not allowed to perform this action on this engagement.");
    }
}

/// <summary>Per-engagement, per-user cache of access answers (short TTL; evicted by EngagementStatusChanged).</summary>
public sealed class EngagementAccessCache(TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(string EngagementId, Guid UserId), (EngagementAccessDto Access, DateTimeOffset ExpiresAt)> entries = new();

    public bool TryGet(string engagementId, Guid userId, out EngagementAccessDto access)
    {
        if (entries.TryGetValue((engagementId, userId), out var entry) && entry.ExpiresAt > clock.GetUtcNow())
        {
            access = entry.Access;
            return true;
        }
        access = null!;
        return false;
    }

    public void Set(string engagementId, Guid userId, EngagementAccessDto access)
        => entries[(engagementId, userId)] = (access, clock.GetUtcNow().Add(Ttl));

    public void Evict(string engagementId)
    {
        foreach (var key in entries.Keys.Where(k => k.EngagementId == engagementId).ToList())
            entries.TryRemove(key, out _);
    }
}

public sealed class EngagementAccessClient(
    HttpClient http,
    EngagementAccessCache cache,
    ICurrentUser currentUser,
    IHttpContextAccessor accessor) : IEngagementAccessClient
{
    public async Task<EngagementAccessDto> GetAccessAsync(string engagementId, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (cache.TryGet(engagementId, userId, out var cached))
            return cached;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/engagements/{Uri.EscapeDataString(engagementId)}/access");
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization) && AuthenticationHeaderValue.TryParse(authorization, out var header))
            request.Headers.Authorization = header;

        using var response = await http.SendAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                throw new ForbiddenException("You are not a participant of this engagement.");
            case HttpStatusCode.NotFound:
                throw new NotFoundException($"Engagement {engagementId} not found.");
        }
        response.EnsureSuccessStatusCode();

        var access = await response.Content.ReadFromJsonAsync<EngagementAccessDto>(JsonDefaults.Web, cancellationToken)
                     ?? throw new HttpRequestException("Empty access response from engagement-service.");
        cache.Set(engagementId, userId, access);
        return access;
    }
}

/// <summary>Drops cached access answers when an engagement changes status (capabilities depend on status).</summary>
public sealed class AccessCacheEvictionHandler(EngagementAccessCache cache) : IEventHandler<EngagementStatusChangedPayload>
{
    public Task HandleAsync(EventEnvelope<EngagementStatusChangedPayload> envelope, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        cache.Evict(envelope.EngagementId);
        return Task.CompletedTask;
    }
}

public static class EngagementAccessExtensions
{
    /// <summary>
    /// Registers the access client (needs <c>Services:Engagement:BaseUrl</c>), its cache, and the status-changed handler
    /// that evicts it. Call after AddAuditFlowAuthentication.
    /// </summary>
    public static WebApplicationBuilder AddEngagementAccessClient(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EngagementAccessCache>();
        builder.Services.AddHttpClient<IEngagementAccessClient, EngagementAccessClient>(client =>
        {
            client.BaseAddress = builder.ServiceUrl("Engagement", "http://localhost:5002");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddEventHandler<EngagementStatusChangedPayload, AccessCacheEvictionHandler>(EventTypes.EngagementStatusChanged);
        return builder;
    }
}
