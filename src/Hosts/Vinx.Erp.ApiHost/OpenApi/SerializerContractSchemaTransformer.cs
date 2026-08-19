using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Reflete no schema as opções de serializer do contrato estrito do host:
/// objetos com <see cref="JsonUnmappedMemberHandling.Disallow"/> recebem
/// <c>additionalProperties: false</c>, já que campos desconhecidos são
/// rejeitados no binding. Genérico para qualquer tipo (request ou response).
/// </summary>
public sealed class SerializerContractSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return Task.CompletedTask;
        }

        // O pipeline de schema usa as opções do framework (HttpJsonOptions); o
        // contrato de binding estrito é declarado no JsonOptions do MVC.
        var jsonOptions = context.ApplicationServices
            .GetRequiredService<IOptions<MvcJsonOptions>>()
            .Value;
        if (
            jsonOptions.JsonSerializerOptions.UnmappedMemberHandling
            == JsonUnmappedMemberHandling.Disallow
        )
        {
            schema.AdditionalPropertiesAllowed = false;
        }

        return Task.CompletedTask;
    }
}
