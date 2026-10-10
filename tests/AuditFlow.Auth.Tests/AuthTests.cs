using System.Security.Cryptography;
using AuditFlow.Auth.Api.Data;
using AuditFlow.Auth.Api.Security;
using AuditFlow.BuildingBlocks.Security;
using AuditFlow.Contracts.Api;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AuditFlow.Auth.Tests;

public class TestKeys : ISigningKeyService
{
    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test-key" };
    public Task<RsaSecurityKey> GetKeyAsync(CancellationToken ct) => Task.FromResult(key);
}

public class TokenServiceTests
{
    private static readonly UserRow Alice = new(DemoData.Alice.UserId, DemoData.Alice.Email, DemoData.Alice.DisplayName, "x", "Auditor");

    private static (TokenService Service, TestKeys Keys) Create(int minutes = 60)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:TokenMinutes"] = minutes.ToString() }).Build();
        var keys = new TestKeys();
        return (new TokenService(keys, TimeProvider.System, config), keys);
    }

    private static async Task<TokenValidationResult> ValidateAsync(string token, TestKeys keys) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = TokenConstants.Issuer,
            ValidAudience = TokenConstants.Audience,
            IssuerSigningKey = await keys.GetKeyAsync(default),
            NameClaimType = TokenConstants.NameClaim,
            RoleClaimType = TokenConstants.RoleClaim
        });

    [Fact]
    public async Task Token_is_signed_and_carries_identity_and_home_role_only()
    {
        var (service, keys) = Create();

        var (token, _) = await service.CreateAsync(Alice, default);
        var result = await ValidateAsync(token, keys);

        Assert.True(result.IsValid);
        var claims = result.Claims;
        Assert.Equal(Alice.UserId.ToString(), claims["sub"]);
        Assert.Equal("Alice Auditor", claims[TokenConstants.NameClaim]);
        Assert.Equal("Auditor", claims[TokenConstants.RoleClaim]);
        Assert.DoesNotContain("engagements", claims.Keys); // per-engagement roles never live in the token
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_rejected()
    {
        var (service, _) = Create();
        var (token, _) = await service.CreateAsync(Alice, default);

        var result = await ValidateAsync(token, new TestKeys());

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Expiry_follows_configuration()
    {
        var (service, _) = Create(minutes: 5);

        var (_, expires) = await service.CreateAsync(Alice, default);

        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }
}

public class LoginServiceTests
{
    private readonly Mock<IUserRepository> users = new();
    private readonly PasswordHasher<UserRow> hasher = new();
    private readonly LoginService login;
    private readonly UserRow alice;

    public LoginServiceTests()
    {
        alice = new UserRow(DemoData.Alice.UserId, DemoData.Alice.Email, DemoData.Alice.DisplayName, "", "Auditor");
        alice = alice with { PasswordHash = hasher.HashPassword(alice, "Passw0rd!") };
        users.Setup(u => u.FindByEmailAsync("alice@auditflow.test", It.IsAny<CancellationToken>())).ReturnsAsync(alice);

        var config = new ConfigurationBuilder().Build();
        login = new LoginService(users.Object, new TokenService(new TestKeys(), TimeProvider.System, config), hasher);
    }

    [Fact]
    public async Task Valid_credentials_return_a_token_and_the_user()
    {
        var response = await login.LoginAsync(new LoginRequest("alice@auditflow.test", "Passw0rd!"), default);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrEmpty(response.AccessToken));
        Assert.Equal(EngagementRole.Auditor, response.User.HomeRole);
    }

    [Fact]
    public async Task Email_is_trimmed_and_case_insensitive()
    {
        var response = await login.LoginAsync(new LoginRequest("  ALICE@AuditFlow.test ", "Passw0rd!"), default);

        Assert.NotNull(response);
    }

    [Theory]
    [InlineData("alice@auditflow.test", "wrong")]
    [InlineData("alice@auditflow.test", "")]
    [InlineData("nobody@auditflow.test", "Passw0rd!")]
    [InlineData("", "Passw0rd!")]
    public async Task Bad_credentials_return_null_without_distinguishing_the_cause(string email, string password)
    {
        var response = await login.LoginAsync(new LoginRequest(email, password), default);

        Assert.Null(response);
    }
}

public class DemoUserSeederTests
{
    [Fact]
    public async Task Seeds_one_user_per_demo_identity_with_hashed_passwords_when_empty()
    {
        var inserted = new List<UserRow>();
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        users.Setup(u => u.InsertAsync(It.IsAny<UserRow>(), It.IsAny<CancellationToken>()))
            .Callback<UserRow, CancellationToken>((r, _) => inserted.Add(r)).Returns(Task.CompletedTask);
        var hasher = new PasswordHasher<UserRow>();

        await new DemoUserSeeder(users.Object, hasher, NullLogger<DemoUserSeeder>.Instance).SeedAsync(default);

        Assert.Equal(DemoData.Users.Count, inserted.Count);
        Assert.All(inserted, u =>
        {
            Assert.NotEqual(DemoData.Password, u.PasswordHash);
            Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(u, u.PasswordHash, DemoData.Password));
        });
        Assert.Contains(inserted, u => u.HomeRole == "Viewer");
        Assert.Equal(2, inserted.Count(u => u.HomeRole == "Reviewer"));
    }

    [Fact]
    public async Task Does_nothing_when_users_already_exist()
    {
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);

        await new DemoUserSeeder(users.Object, new PasswordHasher<UserRow>(), NullLogger<DemoUserSeeder>.Instance).SeedAsync(default);

        users.Verify(u => u.InsertAsync(It.IsAny<UserRow>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
