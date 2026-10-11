using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Eventing;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Clients;

/// <summary>Asks workflow-service whether the engagement passes its required validations (the workflow owns the rules).</summary>
public interface IWorkflowReadinessClient
{
    Task<SubmissionReadinessDto> GetReadinessAsync(string engagementId, CancellationToken ct);
}

public sealed class WorkflowReadinessClient(HttpClient http, IHttpContextAccessor accessor) : IWorkflowReadinessClient
{
    public async Task<SubmissionReadinessDto> GetReadinessAsync(string engagementId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/internal/engagements/{Uri.EscapeDataString(engagementId)}/submission-readiness");
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization) && AuthenticationHeaderValue.TryParse(authorization, out var header))
            request.Headers.Authorization = header;

        using var response = await http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden: throw new ForbiddenException("You are not allowed to submit this engagement.");
            case HttpStatusCode.NotFound: throw new NotFoundException("The engagement workflow is not available yet.");
        }
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SubmissionReadinessDto>(JsonDefaults.Web, ct)
               ?? throw new HttpRequestException("Empty readiness response from workflow-service.");
    }
}
