using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Domain;

/// <summary>Allowed values for the create form. Bound from configuration (Engagement:Catalogue); served to the client.</summary>
public sealed class CreationCatalogue
{
    public List<string> PeriodTypes { get; set; } = [];
    public List<string> Regions { get; set; } = [];
    public List<string> ConfigTypes { get; set; } = [];
    public List<CreationOptionDto> Options { get; set; } = [];

    public CreationOptionsDto ToDto() => new(PeriodTypes, Regions, ConfigTypes, Options);
}

/// <summary>Request-shape rules that need no database or other service. User-dependent rules live in the create service.</summary>
public static class CreateEngagementValidator
{
    public const int MaxNameLength = 200;

    public static Dictionary<string, string[]> Validate(CreateEngagementRequest request, CreationCatalogue catalogue, Guid creatorId)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) Add("name", "Name is required.");
        else if (name.Length > MaxNameLength) Add("name", $"Name must be at most {MaxNameLength} characters.");

        if (string.IsNullOrWhiteSpace(request.PeriodType)) Add("periodType", "Period type is required.");
        else if (!catalogue.PeriodTypes.Contains(request.PeriodType)) Add("periodType", "Unknown period type.");

        if (string.IsNullOrWhiteSpace(request.Region)) Add("region", "Region is required.");
        else if (!catalogue.Regions.Contains(request.Region)) Add("region", "Unknown region.");

        if (request.ReviewerUserId == Guid.Empty) Add("reviewerUserId", "A reviewer is required.");
        else if (request.ReviewerUserId == creatorId) Add("reviewerUserId", "The reviewer cannot be the creator.");

        var configTypes = request.Configuration?.ConfigTypes ?? [];
        if (configTypes.Count == 0) Add("configuration.configTypes", "Select at least one configuration type.");
        else
        {
            if (configTypes.Any(t => !catalogue.ConfigTypes.Contains(t))) Add("configuration.configTypes", "Unknown configuration type.");
            if (configTypes.Distinct().Count() != configTypes.Count) Add("configuration.configTypes", "Configuration types must be unique.");
        }

        var validOptionKeys = catalogue.Options.Select(o => o.Key).ToHashSet();
        if (request.Configuration?.Options?.Keys.Any(k => !validOptionKeys.Contains(k)) == true)
            Add("configuration.options", "Unknown configuration option.");

        var participants = request.ParticipantUserIds ?? [];
        if (participants.Any(p => p == Guid.Empty)) Add("participantUserIds", "Invalid participant.");
        if (participants.Distinct().Count() != participants.Count) Add("participantUserIds", "Participants must be unique.");
        if (participants.Contains(creatorId)) Add("participantUserIds", "The creator is added automatically.");
        if (request.ReviewerUserId != Guid.Empty && participants.Contains(request.ReviewerUserId))
            Add("participantUserIds", "The reviewer is set separately and cannot also be a participant.");

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }
}
