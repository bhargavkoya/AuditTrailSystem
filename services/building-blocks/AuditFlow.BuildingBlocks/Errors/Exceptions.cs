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

/// <summary>Optimistic concurrency conflict (row version mismatch). Maps to 409.</summary>
public sealed class ConcurrencyException(string? currentVersion = null) : Exception("The resource was modified by someone else.")
{
    public string? CurrentVersion { get; } = currentVersion;
}

public sealed class AuditFlowExceptionHandler(ILogger<AuditFlowExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ValidationFailedException v => new ValidationProblemDetails(v.Errors) { Status = 422, Title = "Validation failed" },
            ForbiddenException f => new ProblemDetails { Status = 403, Title = "Forbidden", Detail = f.Message },
            NotFoundException n => new ProblemDetails { Status = 404, Title = "Not found", Detail = n.Message },
            ConcurrencyException c => WithVersion(new ProblemDetails { Status = 409, Title = "Conflict", Detail = c.Message }, c.CurrentVersion),
            BadHttpRequestException b => new ProblemDetails { Status = 400, Title = "Bad request", Detail = b.Message },
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

    private static ProblemDetails WithVersion(ProblemDetails p, string? version)
    {
        if (version is not null) p.Extensions["currentVersion"] = version;
        return p;
    }
}
