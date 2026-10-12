using System.Text;
using AuditFlow.BuildingBlocks.Errors;
using AuditFlow.BuildingBlocks.Messaging;
using AuditFlow.BuildingBlocks.Secrets;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AuditFlow.BuildingBlocks.Tests;

public class ErrorResponseTests
{
    private static async Task<(int Status, string Body)> Handle(Exception exception)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await new AuditFlowExceptionHandler(NullLogger<AuditFlowExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    [Fact]
    public async Task Unexpected_exceptions_return_a_generic_500_without_internals()
    {
        Exception thrown;
        try { throw new InvalidOperationException("Server=prod-sql;Password=hunter2"); }
        catch (Exception ex) { thrown = ex; }

        var (status, body) = await Handle(thrown);

        Assert.Equal(500, status);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("Server=", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("   at ", body);        // no stack frames
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unexpected error", body);
    }

    [Fact]
    public async Task Unavailable_dependencies_return_502_without_the_inner_error()
    {
        var (status, body) = await Handle(new HttpRequestException("connection refused to http://workflow-service:8080"));

        Assert.Equal(502, status);
        Assert.DoesNotContain("workflow-service", body);
    }
}

public class DeadLetterTests
{
    [Theory]
    [InlineData(1, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    public void The_delivery_that_exhausts_the_retries_is_the_last_one(int delivery, int max, bool last)
        => Assert.Equal(last, ServiceBusSubscriberHost.IsLastDelivery(delivery, max));
}

public class SecretProviderTests
{
    private static ConfigurationSecretProvider Config(string? value)
    {
        var data = value is null ? [] : new Dictionary<string, string?> { ["Secrets:Auth:SigningKeyPem"] = value };
        return new ConfigurationSecretProvider(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
    }

    /// <summary>The SDK has two GetSecretAsync overloads; whichever the provider binds to, both answer the same way.</summary>
    private static Mock<SecretClient> Vault(Func<string, bool> matches, Func<Task<Response<KeyVaultSecret>>> answer)
    {
        var vault = new Mock<SecretClient>();
        vault.Setup(c => c.GetSecretAsync(It.Is<string>(n => matches(n)), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(answer);
        vault.Setup(c => c.GetSecretAsync(It.Is<string>(n => matches(n)), It.IsAny<string>(), It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>())).Returns(answer);
        return vault;
    }

    [Fact]
    public async Task Key_vault_wins_over_configuration_when_it_has_the_secret()
    {
        var vault = Vault(n => n == "Auth-SigningKeyPem", () => Task.FromResult(
            Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties("Auth-SigningKeyPem"), "from-vault"), Mock.Of<Response>())));

        var value = await new KeyVaultSecretProvider(vault.Object, Config("from-config")).GetSecretAsync("Auth:SigningKeyPem", default);

        Assert.Equal("from-vault", value); // names map ':' to '-' for Key Vault
    }

    [Fact]
    public async Task Configuration_is_the_fallback_when_the_vault_does_not_have_the_secret()
    {
        var vault = Vault(n => n == "Auth-SigningKeyPem", () => throw new RequestFailedException(404, "not found"));

        var value = await new KeyVaultSecretProvider(vault.Object, Config("from-config")).GetSecretAsync("Auth:SigningKeyPem", default);

        Assert.Equal("from-config", value);
    }

    [Fact]
    public async Task Other_vault_failures_are_not_hidden_behind_the_fallback()
    {
        var vault = Vault(_ => true, () => throw new RequestFailedException(403, "forbidden"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            new KeyVaultSecretProvider(vault.Object, Config("from-config")).GetSecretAsync("Auth:SigningKeyPem", default));
    }

    [Fact]
    public async Task Without_a_vault_the_secret_comes_from_configuration_and_is_null_when_absent()
    {
        Assert.Equal("local", await Config("local").GetSecretAsync("Auth:SigningKeyPem", default));
        Assert.Null(await Config(null).GetSecretAsync("Auth:SigningKeyPem", default));
    }
}
