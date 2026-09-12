namespace QuotesApi.Auth;

// Populated from an Entra app registration you create in the Azure Portal - not
// something this code can provide values for. Section is optional: if it's absent
// from configuration, the Entra scheme simply isn't registered and every token is
// validated against the internal (HS256) scheme only.
public class EntraOptions
{
    public const string SectionName = "Entra";

    public required string TenantId { get; set; }
    public required string Audience { get; set; }
}
