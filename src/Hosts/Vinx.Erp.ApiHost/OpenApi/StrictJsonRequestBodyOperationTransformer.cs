using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Normaliza o corpo JSON de toda operação com body tipado para o contrato
/// estrito do ApiHost: um único content-type application/json e o schema
/// INLINE inferido do model anotado (sem $ref, sem content-types adicionais que
/// a validação de fronteira rejeita). Endpoints com parse manual declaram o tipo
/// do corpo via <see cref="JsonRequestBodyContract"/> (metadata de documento,
/// sem mudar o binding de runtime).
/// </summary>
public sealed class StrictJsonRequestBodyOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var bodyParameter = context.Description.ParameterDescriptions.FirstOrDefault(
            parameter => parameter.Source == BindingSource.Body
        );
        var contract = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<JsonRequestBodyContract>()
            .FirstOrDefault();
        var required = true;
        if (operation.RequestBody is not null)
        {
            // Body tipado (MVC ou minimal API): normaliza o schema inferido.
            required = operation.RequestBody.Required;
            if (bodyParameter?.Type is not { } bodyType)
            {
                return;
            }

            operation.RequestBody = await BuildRequestBodyAsync(
                context,
                bodyType,
                bodyParameter,
                required,
                cancellationToken
            );
            return;
        }

        // Parse manual: o contrato é declarado por metadata; sem tipo, nada a declarar.
        if (contract is null)
        {
            return;
        }

        operation.RequestBody = await BuildRequestBodyAsync(
            context,
            contract.RequestType,
            bodyParameter: null,
            required,
            cancellationToken
        );
    }

    private static async Task<OpenApiRequestBody> BuildRequestBodyAsync(
        OpenApiOperationTransformerContext context,
        Type bodyType,
        Microsoft.AspNetCore.Mvc.ApiExplorer.ApiParameterDescription? bodyParameter,
        bool required,
        CancellationToken cancellationToken
    )
    {
        var schema = await context.GetOrCreateSchemaAsync(
            bodyType,
            bodyParameter,
            cancellationToken
        );
        return new OpenApiRequestBody
        {
            Required = required,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType { Schema = schema },
            },
        };
    }
}
