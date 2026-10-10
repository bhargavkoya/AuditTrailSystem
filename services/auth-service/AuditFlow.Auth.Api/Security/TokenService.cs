using AuditFlow.Auth.Api.Data;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuditFlow.Auth.Api.Security;

public interface ITokenService
{
    Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(UserRow user, CancellationToken ct);
}

public sealed class TokenService(ISigningKeyService keys, TimeProvider clock, IConfiguration configuration) : ITokenService
{
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(UserRow user, CancellationToken ct)
    {
        var key = await keys.GetKeyAsync(ct);
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(configuration.GetValue("Auth:TokenMinutes", 60));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = TokenConstants.Issuer,
            Audience = TokenConstants.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            // Only identity + home role. Per-engagement roles are resolved by engagement-service, never baked into the token.
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.UserId.ToString(),
                [TokenConstants.NameClaim] = user.DisplayName,
                ["email"] = user.Email,
                [TokenConstants.RoleClaim] = user.HomeRole
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

public sealed class LoginService(IUserRepository users, ITokenService tokens, IPasswordHasher<UserRow> hasher)
{
    /// <summary>Returns null for an unknown email or wrong password (callers must not reveal which).</summary>
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return null;

        var user = await users.FindByEmailAsync(request.Email.Trim().ToLowerInvariant(), ct);
        if (user is null)
            return null;

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return null;

        var (token, expires) = await tokens.CreateAsync(user, ct);
        return new LoginResponse(token, expires, new CurrentUserDto(user.UserId, user.DisplayName, user.Email, user.Role));
    }
}
