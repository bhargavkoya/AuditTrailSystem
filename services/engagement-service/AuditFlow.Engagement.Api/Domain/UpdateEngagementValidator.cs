using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Domain;

/// <summary>Request-shape rules for PATCH /engagements/{id}. Null fields mean "unchanged". User-dependent rules live in the service.</summary>
public static class UpdateEngagementValidator
{
    public static Dictionary<string, string[]> Validate(UpdateEngagementRequest request, CreationCatalogue catalogue, Guid ownerId)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.Name is null && request.PeriodType is null && request.Region is null && request.ReviewerUserId is null)
            errors["request"] = ["Provide at least one field to change."];

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0) errors["name"] = ["Name is required."];
            else if (name.Length > CreateEngagementValidator.MaxNameLength)
                errors["name"] = [$"Name must be at most {CreateEngagementValidator.MaxNameLength} characters."];
        }

        if (request.PeriodType is not null && !catalogue.PeriodTypes.Contains(request.PeriodType))
            errors["periodType"] = ["Unknown period type."];

        if (request.Region is not null && !catalogue.Regions.Contains(request.Region))
            errors["region"] = ["Unknown region."];

        if (request.ReviewerUserId is { } reviewer)
        {
            if (reviewer == Guid.Empty) errors["reviewerUserId"] = ["A reviewer is required."];
            else if (reviewer == ownerId) errors["reviewerUserId"] = ["The reviewer cannot be the engagement owner."];
        }

        return errors;
    }
}
