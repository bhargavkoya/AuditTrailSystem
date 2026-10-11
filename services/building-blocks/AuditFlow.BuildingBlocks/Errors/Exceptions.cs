using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AuditFlow.BuildingBlocks.Errors;

/// <summary>Request failed validation (field errors). Maps to 422.</summary>
public sealed class ValidationFailedException(IDictionary<string, string[]> errors) : Exception("Validation failed")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public ValidationFailedException(string field, string message) : this(new Dictionary<string, string[]> { [field] = [message] }) { }
}

/// <summary>Caller is authenticated but not allowed. Maps to 403.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Submission refused because required validations fail. Maps to 422 with the blocking issues listed.</summary>
public sealed class SubmissionBlockedException(IReadOnlyList<AuditFlow.Contracts.Api.IssueDto> issues, string message)
    : Exception(message)
{
    public IReadOnlyList<AuditFlow.Contracts.Api.IssueDto> Issues { get; } = issues;
}

/// <summary>Optimistic concurrency conflict (row version mismatch). Maps to 409.</summary>
public sealed class ConcurrencyException(string? currentVersion = null, string? resource = null) : Exception("The resource was modified by someone else.")
{
    public string? CurrentVersion { get; } = currentVersion;

    /// <summary>What conflicted (e.g. a section key) when a request touches several resources.</summary>
    public string? Resource { get; } = resource;
}

public sealed class AuditFlowExceptionHandler(ILogger<AuditFlowExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ValidationFailedException v => new ValidationProblemDetails(v.Errors) { Status = 422, Title = "Validation failed" },
            SubmissionBlockedException s => WithIssues(new ProblemDetails { Status = 422, Title = "Submission blocked", Detail = s.Message }, s.Issues),
            ForbiddenException f => new ProblemDetails { Status = 403, Title = "Forbidden", Detail = f.Message },
            NotFoundException n => new ProblemDetails { Status = 404, Title = "Not found", Detail = n.Message },
            ConcurrencyException c => WithVersion(new ProblemDetails { Status = 409, Title = "Conflict", Detail = c.Message }, c.CurrentVersion, c.Resource),
            BadHttpRequestException b => new ProblemDetails { Status = 400, Title = "Bad request", Detail = b.Message },
            HttpRequestException => new ProblemDetails { Status = 502, Title = "A dependent service is unavailable" },
            _ => null
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception");
            problem = new ProblemDetails { Status = 500, Title = "Unexpected error" };
        }

        problem.Extensions["correlationId"] = context.Request.Headers["X-Correlation-Id"].ToString();
        context.Response.StatusCode = problem.Status!.Value;
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }

    private static ProblemDetails WithIssues(ProblemDetails p, IReadOnlyList<AuditFlow.Contracts.Api.IssueDto> issues)
    {
        p.Extensions["issues"] = issues;
        return p;
    }

    private static ProblemDetails WithVersion(ProblemDetails p, string? version, string? resource)
    {
        if (version is not null) p.Extensions["currentVersion"] = version;
        if (resource is not null) p.Extensions["resource"] = resource;
        return p;
    }
}

