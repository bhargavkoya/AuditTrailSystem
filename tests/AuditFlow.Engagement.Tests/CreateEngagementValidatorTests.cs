using AuditFlow.Contracts.Api;
using AuditFlow.Engagement.Api.Domain;

namespace AuditFlow.Engagement.Tests;

public class CreateEngagementValidatorTests
{
    private static readonly Guid Creator = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();

    private static readonly CreationCatalogue Catalogue = new()
    {
        PeriodTypes = ["Quarterly", "Annual"],
        Regions = ["EMEA", "APAC"],
        ConfigTypes = ["Equities", "FX"],
        Options = [new("includeReconciliation", "Include reconciliation", "bool")]
    };

    private static CreateEngagementRequest Valid() => new(
        "FY26 Equities audit", "Quarterly", "EMEA", Reviewer, [Guid.NewGuid()],
        new ConfigurationRequest(["Equities"], new Dictionary<string, bool> { ["includeReconciliation"] = true }));

    private static Dictionary<string, string[]> Run(CreateEngagementRequest request) =>
        CreateEngagementValidator.Validate(request, Catalogue, Creator);

    [Fact]
    public void A_valid_request_has_no_errors() => Assert.Empty(Run(Valid()));

    [Fact]
    public void Participants_and_options_are_optional()
        => Assert.Empty(Run(Valid() with { ParticipantUserIds = null, Configuration = new(["FX"], null) }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string name) => Assert.Contains("name", Run(Valid() with { Name = name }).Keys);

    [Fact]
    public void Name_has_a_maximum_length()
        => Assert.Contains("name", Run(Valid() with { Name = new string('x', 201) }).Keys);

    [Theory]
    [InlineData("", "periodType")]
    [InlineData("Weekly", "periodType")]
    public void Period_type_must_be_in_the_catalogue(string value, string field)
        => Assert.Contains(field, Run(Valid() with { PeriodType = value }).Keys);

    [Theory]
    [InlineData("")]
    [InlineData("MARS")]
    public void Region_must_be_in_the_catalogue(string value)
        => Assert.Contains("region", Run(Valid() with { Region = value }).Keys);

    [Fact]
    public void Reviewer_is_mandatory()
        => Assert.Contains("reviewerUserId", Run(Valid() with { ReviewerUserId = Guid.Empty }).Keys);

    [Fact]
    public void Reviewer_cannot_be_the_creator()
        => Assert.Contains("reviewerUserId", Run(Valid() with { ReviewerUserId = Creator }).Keys);

    [Fact]
    public void At_least_one_configuration_type_is_required()
        => Assert.Contains("configuration.configTypes", Run(Valid() with { Configuration = new([], null) }).Keys);

    [Fact]
    public void Configuration_types_must_be_known_and_unique()
    {
        Assert.Contains("configuration.configTypes", Run(Valid() with { Configuration = new(["Crypto"], null) }).Keys);
        Assert.Contains("configuration.configTypes", Run(Valid() with { Configuration = new(["FX", "FX"], null) }).Keys);
    }

    [Fact]
    public void Unknown_options_are_rejected()
        => Assert.Contains("configuration.options",
            Run(Valid() with { Configuration = new(["FX"], new Dictionary<string, bool> { ["nope"] = true }) }).Keys);

    [Fact]
    public void Participants_must_be_unique_and_exclude_the_creator_and_reviewer()
    {
        var dup = Guid.NewGuid();
        Assert.Contains("participantUserIds", Run(Valid() with { ParticipantUserIds = [dup, dup] }).Keys);
        Assert.Contains("participantUserIds", Run(Valid() with { ParticipantUserIds = [Creator] }).Keys);
        Assert.Contains("participantUserIds", Run(Valid() with { ParticipantUserIds = [Reviewer] }).Keys);
        Assert.Contains("participantUserIds", Run(Valid() with { ParticipantUserIds = [Guid.Empty] }).Keys);
    }

    [Fact]
    public void Several_problems_are_reported_together()
    {
        var errors = Run(new CreateEngagementRequest("", "x", "y", Guid.Empty, null, new([], null)));

        Assert.True(errors.Count >= 5);
    }
}
