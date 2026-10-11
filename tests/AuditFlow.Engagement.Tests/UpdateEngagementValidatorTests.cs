using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Domain;

namespace AuditFlow.Engagement.Tests;

public class UpdateEngagementValidatorTests
{
    private static readonly CreationCatalogue Catalogue = new()
    {
        PeriodTypes = ["Quarterly", "Annual"],
        Regions = ["EMEA", "APAC"]
    };

    private static readonly Guid Owner = Guid.NewGuid();

    private static Dictionary<string, string[]> Validate(UpdateEngagementRequest request)
        => UpdateEngagementValidator.Validate(request, Catalogue, Owner);

    [Fact]
    public void An_empty_request_is_rejected() => Assert.Contains("request", Validate(new(null, null, null, null)).Keys);

    [Fact]
    public void Valid_partial_updates_pass()
    {
        Assert.Empty(Validate(new("New name", null, null, null)));
        Assert.Empty(Validate(new(null, "Annual", "APAC", null)));
        Assert.Empty(Validate(new(null, null, null, Guid.NewGuid())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_are_rejected(string name) => Assert.Contains("name", Validate(new(name, null, null, null)).Keys);

    [Fact]
    public void Overlong_names_are_rejected() =>
        Assert.Contains("name", Validate(new(new string('x', CreateEngagementValidator.MaxNameLength + 1), null, null, null)).Keys);

    [Fact]
    public void Unknown_catalogue_values_are_rejected()
    {
        var errors = Validate(new(null, "Weekly", "Mars", null));

        Assert.Contains("periodType", errors.Keys);
        Assert.Contains("region", errors.Keys);
    }

    [Fact]
    public void The_reviewer_cannot_be_empty_or_the_owner()
    {
        Assert.Contains("reviewerUserId", Validate(new(null, null, null, Guid.Empty)).Keys);
        Assert.Contains("reviewerUserId", Validate(new(null, null, null, Owner)).Keys);
    }
}

public class EditDetailsPolicyTests
{
    [Theory]
    [InlineData(EngagementRole.Auditor, EngagementStatus.Draft, true)]
    [InlineData(EngagementRole.CoAuditor, EngagementStatus.Draft, true)]
    [InlineData(EngagementRole.Reviewer, EngagementStatus.Draft, false)]
    [InlineData(EngagementRole.Viewer, EngagementStatus.Draft, false)]
    [InlineData(EngagementRole.Auditor, EngagementStatus.InProgress, false)]
    [InlineData(EngagementRole.Auditor, EngagementStatus.UnderReview, false)]
    [InlineData(EngagementRole.Auditor, EngagementStatus.Approved, false)]
    [InlineData(EngagementRole.Auditor, EngagementStatus.Rejected, false)]
    public void Only_editors_of_a_draft_may_edit_details(EngagementRole role, EngagementStatus status, bool expected)
    {
        Assert.Equal(expected, PermissionPolicy.CanEditDetails(role, status));
        Assert.Equal(expected, PermissionPolicy.AllowedActionsFor(role, status).Contains(EngagementActions.EditDetails));
    }
}
