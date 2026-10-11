using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Review.Api.Clients;

/// <summary>The engagement header (status, role, capabilities) from engagement-service, the permission authority.</summary>
public interface IEngagementClient
{
    Task<EngagementDetailDto> GetAsync(string engagementId, CancellationToken ct);
}

/// <summary>Read-only tab content and validation summary from workflow-service; the review screen renders them as is.</summary>
public interface IWorkflowViewClient
{
    Task<TabsDto> GetTabsAsync(string engagementId, CancellationToken ct);
    Task<TabViewDto> GetTabAsync(string engagementId, int tabNo, CancellationToken ct);
    Task<ValidationSummaryDto> GetValidationAsync(string engagementId, CancellationToken ct);
}

/// <summary>Forwards the caller's token so each upstream service makes its own authorization decision.</summary>
internal static class Forwarding
{
    public static async Task<T> GetAsync<T>(HttpClient http, IHttpContextAccessor accessor, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization) && AuthenticationHeaderValue.TryParse(authorization, out var header))
            request.Headers.Authorization = header;

        using var response = await http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden: throw new ForbiddenException("You are not a participant of this engagement.");
            case HttpStatusCode.NotFound: throw new NotFoundException("Engagement or workflow not found.");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Web, ct)
               ?? throw new HttpRequestException($"Empty response from {path}.");
    }
}

public sealed class EngagementClient(HttpClient http, IHttpContextAccessor accessor) : IEngagementClient
{
    public Task<EngagementDetailDto> GetAsync(string engagementId, CancellationToken ct)
        => Forwarding.GetAsync<EngagementDetailDto>(http, accessor, $"/engagements/{Uri.EscapeDataString(engagementId)}", ct);
}

public sealed class WorkflowViewClient(HttpClient http, IHttpContextAccessor accessor) : IWorkflowViewClient
{
    public Task<TabsDto> GetTabsAsync(string engagementId, CancellationToken ct)
        => Forwarding.GetAsync<TabsDto>(http, accessor, $"/engagements/{Uri.EscapeDataString(engagementId)}/tabs", ct);

    public Task<TabViewDto> GetTabAsync(string engagementId, int tabNo, CancellationToken ct)
        => Forwarding.GetAsync<TabViewDto>(http, accessor, $"/engagements/{Uri.EscapeDataString(engagementId)}/tabs/{tabNo}", ct);

    public Task<ValidationSummaryDto> GetValidationAsync(string engagementId, CancellationToken ct)
        => Forwarding.GetAsync<ValidationSummaryDto>(http, accessor, $"/engagements/{Uri.EscapeDataString(engagementId)}/validation-summary", ct);
}
