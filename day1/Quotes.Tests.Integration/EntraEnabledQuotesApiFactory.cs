using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Quotes.Tests.Integration;

// Same as QuotesApiFactory, plus a fake (structurally valid, not really-Azure)
// "Entra" config section - just enough for AddJwtAuth's
// `if (entraOptions is not null) { authBuilder.AddJwtBearer(EntraSchemeName, ...) }`
// branch to actually run. Registering a JwtBearer scheme with an Authority URL
// doesn't require that URL to be reachable - only per-request token validation
// would need real connectivity, and no test here sends a real Entra token, so
// this proves the DI wiring itself is correct without needing genuine Azure
// credentials this project doesn't have.
public class EntraEnabledQuotesApiFactory : QuotesApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Entra:TenantId"] = "test-tenant",
                ["Entra:Audience"] = "test-audience"
            });
        });
    }
}
