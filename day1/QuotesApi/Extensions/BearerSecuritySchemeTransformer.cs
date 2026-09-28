using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace QuotesApi.Extensions;

// Day 27: without this, the generated OpenAPI document has no security scheme at
// all - every [Authorize] endpoint looks anonymous to any tool (Swagger UI, a
// client generator, a security scanner) reading the spec, even though the API
// itself correctly rejects unauthenticated requests. This is purely a
// documentation fix - it doesn't change what the API accepts - but "the OpenAPI
// surface says less than the API actually requires" is exactly the kind of gap
// this exercise's "harden the OpenAPI surface" is about.
public class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT issued by POST /api/auth/login (or an Entra-issued token, if Entra is configured).",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = scheme;

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        });

        return Task.CompletedTask;
    }
}
