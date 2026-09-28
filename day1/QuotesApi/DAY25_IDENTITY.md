# Day 25 — Identity end-to-end (HashiCorp Vault instead of Azure Key Vault)

Azure Key Vault + Managed Identity need an Azure subscription. **HashiCorp
Vault** (free, open-source, self-hosted — `docker-compose.yml`) plays Key Vault's
role; its **AppRole** auth method plays Managed Identity's — a workload
credential provisioned by infra and read from the environment, never a human's
static key, which is the property that actually matters here. It isn't a perfect
substitute (Managed Identity needs *no* credential material anywhere; AppRole
still needs a RoleId/SecretId pair to exist as an env var) — documented below,
not glossed over.

## A real finding, fixed

Auditing this app for "secrets in app settings" turned up an actual one:
`appsettings.Production.json` had a plaintext, committed JWT `SigningKey` —
exactly the anti-pattern this exercise exists to catch. Fixed as part of this day:

```diff
   "Jwt": {
     "Issuer": "QuotesApi",
     "Audience": "QuotesApi.Clients",
-    "AccessTokenExpirySeconds": 900,
-    "SigningKey": "ymtrUmJVaMq8YMZ/IBaxKoR1hGgwE1Ybl4WdFWWg2yE="
+    "AccessTokenExpirySeconds": 900
   },
+  "_comment_Jwt_SigningKey": "Deliberately absent - set via env var or Vault, never here.",
```

(The file was never actually committed to git — caught before it reached
history — but it's the exact live example the exercise is built to catch, so
it's fixed regardless.)

## The Managed-Identity analog: Vault AppRole

`Extensions/VaultConfigurationExtensions.cs`:

```csharp
public static IConfigurationBuilder AddVaultSecrets(
    this IConfigurationBuilder builder, string vaultAddress, string mountPoint, string secretPath)
{
    var roleId = Environment.GetEnvironmentVariable("VAULT_ROLE_ID");
    var secretId = Environment.GetEnvironmentVariable("VAULT_SECRET_ID");

    if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(secretId))
    {
        throw new InvalidOperationException(
            "Vault:Address is configured but VAULT_ROLE_ID/VAULT_SECRET_ID environment " +
            "variables are not set.");
    }

    IAuthMethodInfo authMethod = new AppRoleAuthMethodInfo(roleId, secretId);
    var vaultClient = new VaultClient(new VaultClientSettings(vaultAddress, authMethod));

    var secret = vaultClient.V1.Secrets.KeyValue.V2
        .ReadSecretAsync(path: secretPath, mountPoint: mountPoint)
        .GetAwaiter().GetResult();

    builder.AddInMemoryCollection(
        secret.Data.Data.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value?.ToString())));
    return builder;
}
```

`Program.cs` — same "opt-in only when configured" shape the Key Vault call had:

```csharp
var vaultAddress = builder.Configuration["Vault:Address"];
if (!string.IsNullOrEmpty(vaultAddress))
{
    builder.Configuration.AddVaultSecrets(vaultAddress, mountPoint: "secret", secretPath: "quotesapi");
}
```

**Where the RoleId/SecretId come from:** provisioned once, by infra, against a
Vault policy scoped to exactly the `secret/quotesapi` path this app reads —
never typed by a person into a config file. Against the dev-mode Vault in
`docker-compose.yml`:

```bash
export VAULT_ADDR=http://localhost:8200
export VAULT_TOKEN=root   # dev-mode root token - never how this looks in real Vault

vault secrets enable -path=secret kv-v2
vault kv put secret/quotesapi Jwt__SigningKey="$(openssl rand -base64 32)"

vault policy write quotesapi-read - <<EOF
path "secret/data/quotesapi" { capabilities = ["read"] }
EOF

vault auth enable approle
vault write auth/approle/role/quotesapi token_policies="quotesapi-read" token_ttl=1h token_max_ttl=4h
vault read auth/approle/role/quotesapi/role-id            # -> VAULT_ROLE_ID
vault write -f auth/approle/role/quotesapi/secret-id       # -> VAULT_SECRET_ID
```

## Where this honestly differs from Managed Identity

Managed Identity's whole point is that *no credential of any kind* needs to
exist for the workload to authenticate — the cloud platform's metadata service
vouches for it based on where it's running, full stop. AppRole is the closest
free, self-hostable analog, but a RoleId/SecretId pair still has to be
provisioned and delivered to the workload somehow (an env var, here) — it moves
the secret from "committed to source" to "delivered by infra at deploy time,"
which is the actual goal of this exercise, but it is not the zero-credential
property Managed Identity gives you inside Azure. Worth naming rather than
quietly eliding.

**Entra ID for app auth** was already in place before this exercise (see
`Auth/AuthenticationExtensions.cs` and `DAY22_RESILIENCE.md`'s Entra backchannel)
— nothing new needed there.

## Proof: zero secrets in app settings

```bash
$ grep -rn "SigningKey" QuotesApi/appsettings*.json
appsettings.Production.json:  "_comment_Jwt_SigningKey": "Deliberately absent - see DAY25_IDENTITY.md. ..."
```

No value, only the comment explaining where it comes from instead. And the app
genuinely boots with it delivered purely as an environment variable — the path
this app's actual production host (MonsterASP, shared hosting with no room for
a Vault sidecar) needs — no committed file, no Vault:

```
$ ASPNETCORE_ENVIRONMENT=Production Jwt__SigningKey="<random-32-bytes>" \
    ASPNETCORE_URLS=http://localhost:5099 dotnet QuotesApi.dll
...
health=200
```

An environment that *can* run a Vault sidecar (a VM, a container platform)
gets the same result by setting `Vault:Address` instead — same app code, no
recompilation, per the opt-in check in `Program.cs`.
