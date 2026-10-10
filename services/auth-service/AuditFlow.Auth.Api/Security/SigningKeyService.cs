using System.Security.Cryptography;
using AuditFlow.BuildingBlocks.Data;
using AuditFlow.BuildingBlocks.Secrets;
using Dapper;
using Microsoft.IdentityModel.Tokens;

namespace AuditFlow.Auth.Api.Security;

public interface ISigningKeyService
{
    Task<RsaSecurityKey> GetKeyAsync(CancellationToken ct);
}

/// <summary>
/// RSA signing key. Source order: secret provider (Key Vault / config "Auth:SigningKeyPem"), then the database,
/// else a newly generated key that is stored (dev convenience: tokens survive restarts).
/// </summary>
public sealed class SigningKeyService(ISecretProvider secrets, IDbConnectionFactory connections, ILogger<SigningKeyService> logger) : ISigningKeyService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RsaSecurityKey? cached;

    public async Task<RsaSecurityKey> GetKeyAsync(CancellationToken ct)
    {
        if (cached is not null) return cached;

        await gate.WaitAsync(ct);
        try
        {
            return cached ??= await LoadAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RsaSecurityKey> LoadAsync(CancellationToken ct)
    {
        var pem = await secrets.GetSecretAsync("Auth:SigningKeyPem", ct);
        if (string.IsNullOrWhiteSpace(pem))
        {
            await using var connection = await connections.OpenAsync(ct);
            pem = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT TOP 1 PrivatePem FROM dbo.SigningKeys ORDER BY CreatedAt DESC", null, ct);

            if (string.IsNullOrWhiteSpace(pem))
            {
                using var generated = RSA.Create(2048);
                pem = generated.ExportPkcs8PrivateKeyPem();
                var kid = KeyIdOf(generated);
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO dbo.SigningKeys (KeyId, PrivatePem) VALUES (@kid, @pem)", new { kid, pem }, cancellationToken: ct));
                logger.LogInformation("Generated a new RSA signing key {KeyId}", kid);
            }
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa) { KeyId = KeyIdOf(rsa) };
    }

    private static string KeyIdOf(RSA rsa)
        => Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
}
