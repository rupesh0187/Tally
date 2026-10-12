using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TallyNext.Api.Infrastructure;

/// <summary>Adds the JWT bearer scheme to the OpenAPI document so Swagger UI can call protected endpoints.</summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access token from POST /api/auth/login",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        });
        document.Info.Title = "TallyNext API";
        document.Info.Description = "Accounting, reporting and device synchronization API.";
        return Task.CompletedTask;
    }
}
