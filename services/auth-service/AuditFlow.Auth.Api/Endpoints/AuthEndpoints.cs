using AuditFlow.Auth.Api.Data;
using AuditFlow.Auth.Api.Security;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace AuditFlow.Auth.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest request, LoginService login, CancellationToken ct) =>
        {
            var result = await login.LoginAsync(request, ct);
            // Same response for unknown email and wrong password.
            return result is null
                ? Results.Problem(statusCode: 401, title: "Invalid email or password")
                : Results.Ok(result);
        }).AllowAnonymous();

        auth.MapGet("/me", async (ICurrentUser user, IUserRepository users, CancellationToken ct) =>
        {
            var row = (await users.GetByIdsAsync([user.UserId], ct)).SingleOrDefault();
            return row is null
                ? Results.Unauthorized()
                : Results.Ok(new CurrentUserDto(row.UserId, row.DisplayName, row.Email, row.Role));
        }).RequireAuthorization();

        auth.MapGet("/permissions", (ICurrentUser user) =>
            new PermissionsDto(user.HomeRole is EngagementRole.Auditor or EngagementRole.CoAuditor))
            .RequireAuthorization();

        // Feeds the reviewer / participant pickers on the create-engagement form.
        auth.MapGet("/users", async (EngagementRole? homeRole, IUserRepository users, CancellationToken ct) =>
            (await users.ListAsync(homeRole, ct)).Select(u => new UserSummaryDto(u.UserId, u.DisplayName, u.Role)))
            .RequireAuthorization();

        // Internal: not routed by the gateway. engagement-service validates participants and snapshots display names.
        app.MapPost("/internal/users/lookup", async ([FromBody] UserLookupRequest request, IUserRepository users, CancellationToken ct) =>
            (await users.GetByIdsAsync(request.UserIds.Distinct().ToList(), ct))
            .Select(u => new UserSummaryDto(u.UserId, u.DisplayName, u.Role)))
            .RequireAuthorization().WithTags("Internal");

        // OIDC discovery + JWKS: what every other service validates tokens against.
        app.MapGet("/.well-known/openid-configuration", (HttpRequest request) =>
        {
            var baseUrl = $"{request.Scheme}://{request.Host}";
            return Results.Ok(new Dictionary<string, object>
            {
                ["issuer"] = TokenConstants.Issuer,
                ["jwks_uri"] = $"{baseUrl}/.well-known/jwks.json",
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["subject_types_supported"] = new[] { "public" },
                ["response_types_supported"] = new[] { "token" }
            });
        }).AllowAnonymous().WithTags("Discovery");

        app.MapGet("/.well-known/jwks.json", async (ISigningKeyService keys, CancellationToken ct) =>
        {
            var key = await keys.GetKeyAsync(ct);
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
            return Results.Ok(new
            {
                keys = new[]
                {
                    new { kty = jwk.Kty, use = "sig", alg = "RS256", kid = key.KeyId, n = jwk.N, e = jwk.E }
                }
            });
        }).AllowAnonymous().WithTags("Discovery");
    }
}
