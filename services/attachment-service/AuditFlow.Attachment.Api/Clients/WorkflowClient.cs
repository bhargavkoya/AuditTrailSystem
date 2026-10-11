using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Attachment.Api.Clients;

/// <summary>Asks workflow-service whether a section exists, is visible and accepts attachments (section scope rules live there).</summary>
public interface IWorkflowClient
{
    Task<SectionPolicyDto> GetSectionPolicyAsync(string engagementId, string sectionKey, CancellationToken ct);
}

public sealed class WorkflowClient(HttpClient http, IHttpContextAccessor accessor) : IWorkflowClient
{
    public async Task<SectionPolicyDto> GetSectionPolicyAsync(string engagementId, string sectionKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/internal/engagements/{Uri.EscapeDataString(engagementId)}/sections/{Uri.EscapeDataString(sectionKey)}");
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization) && AuthenticationHeaderValue.TryParse(authorization, out var header))
            request.Headers.Authorization = header;

        using var response = await http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden: throw new ForbiddenException("You are not a participant of this engagement.");
            case HttpStatusCode.NotFound: throw new NotFoundException("The engagement workflow is not available yet.");
        }
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SectionPolicyDto>(JsonDefaults.Web, ct)
               ?? throw new HttpRequestException("Empty section policy response from workflow-service.");
    }
}
