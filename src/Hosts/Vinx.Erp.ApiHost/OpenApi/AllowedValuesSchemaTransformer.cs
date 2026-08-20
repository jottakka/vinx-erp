using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Reflete no schema a allowlist de <see cref="AllowedValuesAttribute"/>:
/// propriedades de string com valores permitidos recebem <c>enum</c> com os
/// mesmos valores, espelhando a validação declarada no binding (a validação de
/// runtime permanece a cargo do ModelState). Genérico para qualquer tipo
/// (request ou response).
/// </summary>
public sealed class AllowedValuesSchemaTransformer : IOpenApiSchemaTransformer
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

        var properties = schema.Properties;
        if (properties is null)
        {
            return Task.CompletedTask;
        }

        foreach (var property in context.JsonTypeInfo.Properties)
        {
            var allowedValues = property
                .AttributeProvider?.GetCustomAttributes(typeof(AllowedValuesAttribute), inherit: false)
                .OfType<AllowedValuesAttribute>()
                .FirstOrDefault();
            if (allowedValues is null)
            {
                continue;
            }

            var match = properties.FirstOrDefault(entry =>
                string.Equals(entry.Key, property.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (match.Value is not OpenApiSchema propertySchema)
            {
                continue;
            }

            propertySchema.Enum = allowedValues
                .Values.OfType<string>()
                .Select(value => (JsonNode)JsonValue.Create(value)!)
                .ToList();
        }

        return Task.CompletedTask;
    }
}
