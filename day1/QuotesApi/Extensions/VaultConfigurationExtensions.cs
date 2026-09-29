using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.AppRole;

namespace QuotesApi.Extensions;

// Day 25: HashiCorp Vault (free, open-source, self-hosted - see docker-compose.yml)
// in place of Azure Key Vault. Mirrors exactly the shape Program.cs already used
// for AddAzureKeyVault: called once during configuration building, opt-in only
// when an address is configured, and it never touches a plaintext secret - the
// RoleId/SecretId pair below is Vault's AppRole auth method, the closest
// free/self-hostable analog to Managed Identity: a *workload* credential
// (provisioned once by infra, read from the environment, never committed to
// source) rather than a human's static API key. It isn't a perfect substitute -
// Managed Identity needs no credential material anywhere because the cloud
// platform itself vouches for the workload; AppRole still needs a RoleId/SecretId
// pair to exist somewhere (an env var here) - but it gets the property that
// actually matters for this exercise: no secret lives in a committed file.
public static class VaultConfigurationExtensions
{
    public static IConfigurationBuilder AddVaultSecrets(
        this IConfigurationBuilder builder, string vaultAddress, string mountPoint, string secretPath)
    {
        var roleId = Environment.GetEnvironmentVariable("VAULT_ROLE_ID");
        var secretId = Environment.GetEnvironmentVariable("VAULT_SECRET_ID");

        if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(secretId))
        {
            throw new InvalidOperationException(
                "Vault:Address is configured but VAULT_ROLE_ID/VAULT_SECRET_ID environment " +
                "variables are not set. These identify the workload to Vault's AppRole auth " +
                "method and must never be committed - see DAY25_IDENTITY.md.");
        }

        IAuthMethodInfo authMethod = new AppRoleAuthMethodInfo(roleId, secretId);
        var vaultClient = new VaultClient(new VaultClientSettings(vaultAddress, authMethod));

        // Synchronous: configuration building happens before the host's async
        // infrastructure exists, the same constraint AddAzureKeyVault itself is
        // under - there's no "await this during Program.cs startup" option here.
        var secret = vaultClient.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path: secretPath, mountPoint: mountPoint)
            .GetAwaiter().GetResult();

        // Stored in Vault using the env-var convention (Jwt__SigningKey, matching
        // DAY25_IDENTITY.md's own `vault kv put` example) so it reads the same way
        // whether it ends up delivered via Vault or a real env var. But
        // AddInMemoryCollection is not EnvironmentVariablesConfigurationProvider -
        // it stores whatever key it's given literally, it does NOT translate "__"
        // to ":" the way the env-var provider does. Without this translation,
        // config.GetSection("Jwt")["SigningKey"] would never find a key literally
        // named "Jwt__SigningKey", and JwtOptions.SigningKey (required) would be
        // null - not caught until AddJwtAuth throws at startup.
        var values = secret.Data.Data
            .Select(kv => new KeyValuePair<string, string?>(kv.Key.Replace("__", ":"), kv.Value?.ToString()));

        builder.AddInMemoryCollection(values);
        return builder;
    }
}
