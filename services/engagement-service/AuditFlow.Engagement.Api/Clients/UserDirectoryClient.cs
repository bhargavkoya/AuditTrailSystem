using System.Net.Http.Headers;
using AuditFlow.Contracts.Api;

namespace AuditFlow.Engagement.Api.Clients;

/// <summary>Looks users up in auth-service (the only owner of user data). No cross-service DB reads.</summary>
public interface IUserDirectoryClient
{
    Task<IReadOnlyDictionary<Guid, UserSummaryDto>> LookupAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
}

public sealed class UserDirectoryClient(HttpClient http, IHttpContextAccessor accessor) : IUserDirectoryClient
{
    public async Task<IReadOnlyDictionary<Guid, UserSummaryDto>> LookupAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/users/lookup")
        {
            Content = JsonContent.Create(new UserLookupRequest(userIds.Distinct().ToList()))
        };

        // Forward the caller's token; auth-service authenticates the call.
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization))
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var users = await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>(ct) ?? [];
        return users.ToDictionary(u => u.UserId);
    }
}
