using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuditFlow.BuildingBlocks.Secrets;

/// <summary>Where secrets come from. Local: configuration / user-secrets / env. Azure: Key Vault.</summary>
public interface ISecretProvider
{
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Reads "Secrets:{name}" (user-secrets or environment variable Secrets__Name).</summary>
public sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
        => Task.FromResult(configuration[$"Secrets:{name}"]);
}

/// <summary>Key Vault first (names use '-' instead of ':'), configuration as a fallback.</summary>
public sealed class KeyVaultSecretProvider(SecretClient client, ConfigurationSecretProvider fallback) : ISecretProvider
{
    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var secret = await client.GetSecretAsync(name.Replace(':', '-'), cancellationToken: cancellationToken);
            return secret.Value.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return await fallback.GetSecretAsync(name, cancellationToken);
        }
    }
}

public static class SecretExtensions
{
    public static WebApplicationBuilder AddAuditFlowSecrets(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ConfigurationSecretProvider>();

        var vaultUri = builder.Configuration["KeyVault:Uri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            builder.Services.AddSingleton<ISecretProvider>(sp => sp.GetRequiredService<ConfigurationSecretProvider>());
        }
        else
        {
            builder.Services.AddSingleton(new SecretClient(new Uri(vaultUri), new DefaultAzureCredential()));
            builder.Services.AddSingleton<ISecretProvider, KeyVaultSecretProvider>();
        }
        return builder;
    }
}
