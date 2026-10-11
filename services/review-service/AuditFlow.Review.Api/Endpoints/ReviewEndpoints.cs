using AuditFlow.Contracts.Api;
using AuditFlow.Review.Api.Domain;

namespace AuditFlow.Review.Api.Endpoints;

public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/reviews/{engagementId}").WithTags("Review").RequireAuthorization();

        group.MapGet("/", (string engagementId, ReviewService service, CancellationToken ct) => service.GetAsync(engagementId, ct));

        group.MapPost("/comments", async (string engagementId, AddCommentRequest request, ReviewService service, CancellationToken ct) =>
            Results.Created($"/reviews/{engagementId}", await service.AddCommentAsync(engagementId, request, ct)));

        // The status change itself is applied by engagement-service from the ReviewApproved / ReviewRejected event.
        group.MapPost("/approve", async (string engagementId, DecisionRequest? request, ReviewService service, CancellationToken ct)
            => Results.Accepted($"/reviews/{engagementId}", await service.ApproveAsync(engagementId, request, ct)));

        group.MapPost("/reject", async (string engagementId, DecisionRequest? request, ReviewService service, CancellationToken ct)
            => Results.Accepted($"/reviews/{engagementId}", await service.RejectAsync(engagementId, request, ct)));
    }
}
