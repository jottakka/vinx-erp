using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.Foundation.Observability;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>Documenta o header de correlação X-Correlation-ID em toda resposta da API.</summary>
public sealed class CorrelationResponseHeaderOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (operation.Responses is null)
        {
            return Task.CompletedTask;
        }

        var document =
            context.Document
            ?? throw new InvalidOperationException(
                "OpenAPI response contract requires a document context."
            );
        // Os transformers executam antes do registro final dos componentes pelo host.
        document.Workspace ??= new OpenApiWorkspace();
        document.RegisterComponents();
        foreach (var response in operation.Responses.Values)
        {
            var concreteResponse = response switch
            {
                OpenApiResponse direct => direct,
                OpenApiResponseReference reference =>
                    reference.RecursiveTarget
                    ?? throw new InvalidOperationException(
                        "OpenAPI response reference must resolve to a concrete response."
                    ),
                _ => throw new InvalidOperationException(
                    "OpenAPI response must be a concrete response or a resolvable response reference."
                ),
            };

            concreteResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concreteResponse.Headers[RequestCorrelationMiddleware.ResponseHeaderName] =
                new OpenApiHeader
                {
                    Description =
                        "Referência opaca gerada pelo servidor para suporte da requisição.",
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "uuid",
                    },
                };
        }

        return Task.CompletedTask;
    }
}
