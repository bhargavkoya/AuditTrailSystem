using System.Security.Claims;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AuditFlow.BuildingBlocks.Security;

public static class TokenConstants
{
    public const string Issuer = "auditflow-auth";
    public const string Audience = "auditflow";
    public const string NameClaim = "name";
    public const string RoleClaim = "role";
}

/// <summary>The authenticated caller, read from JWT claims. Per-engagement roles are NOT in the token.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    string Name { get; }
    /// <summary>The user's home role (JWT "role" claim). Decides who may create engagements; not an engagement role.</summary>
    EngagementRole HomeRole { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id)
        ? id
        : throw new ForbiddenException("Missing or invalid subject claim.");

    public string Name => Principal?.FindFirstValue(TokenConstants.NameClaim) ?? string.Empty;

    public EngagementRole HomeRole => Enum.TryParse<EngagementRole>(Principal?.FindFirstValue(TokenConstants.RoleClaim), out var role)
        ? role
        : throw new ForbiddenException("Missing or invalid role claim.");
}

/// <summary>Marker so UseAuditFlowDefaults only adds the auth middleware for services that opted in.</summary>
public sealed class AuthenticationMarker;

public static class AuthenticationExtensions
{
    /// <summary>
    /// JWT bearer validation against auth-service's OIDC discovery document (signing keys via JWKS).
    /// Swap in Entra ID later by changing Auth:MetadataAddress / issuer settings only.
    /// </summary>
    public static WebApplicationBuilder AddAuditFlowAuthentication(this WebApplicationBuilder builder)
    {
        var metadataAddress = builder.Configuration["Auth:MetadataAddress"]
                              ?? "http://localhost:5001/.well-known/openid-configuration";

        builder.Services.AddSingleton<AuthenticationMarker>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MetadataAddress = metadataAddress;
                o.RequireHttpsMetadata = false;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenConstants.Issuer,
                    ValidAudience = TokenConstants.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = TokenConstants.NameClaim,
                    RoleClaimType = TokenConstants.RoleClaim
                };
            });
        builder.Services.AddAuthorization();

        builder.Services.Configure<SwaggerGenOptions>(o =>
        {
            o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the accessToken returned by POST /auth/login"
            });
            o.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        });

        return builder;
    }
}
