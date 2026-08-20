using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>
/// Publica o contrato decimal do Paguai no schema OpenAPI: propriedades
/// serializadas pelo <see cref="PaguaiAmountJsonConverter"/> viram texto com
/// padrão e limites de <see cref="PaguaiAmountRules"/>, tanto no request
/// quanto na resposta — a inferência nativa documentaria número. O binding do
/// request (marcado com <see cref="DecimalRangeAttribute"/>) aceita zero a duas
/// casas; a resposta escreve sempre duas.
/// </summary>
public sealed class DecimalRangeSchemaTransformer : IOpenApiSchemaTransformer
{
    private const string RequestAmountPattern = @"^\d+(?:\.\d{1,2})?$";
    private const string ResponseAmountPattern = @"^\d+\.\d{2}$";

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.JsonTypeInfo.Kind is not JsonTypeInfoKind.Object || schema.Properties is null)
        {
            return Task.CompletedTask;
        }

        foreach (var property in context.JsonTypeInfo.Properties)
        {
            var attributes = property.AttributeProvider?.GetCustomAttributes(
                inherit: false
            ) ?? [];
            if (
                !attributes
                    .OfType<JsonConverterAttribute>()
                    .Any(attribute => attribute.ConverterType == typeof(PaguaiAmountJsonConverter))
            )
            {
                continue;
            }

            var match = schema.Properties.FirstOrDefault(entry =>
                string.Equals(entry.Key, property.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (match.Value is OpenApiSchema propertySchema)
            {
                propertySchema.Type = JsonSchemaType.String;
                propertySchema.Pattern = attributes.OfType<DecimalRangeAttribute>().Any()
                    ? RequestAmountPattern
                    : ResponseAmountPattern;
                propertySchema.Minimum = PaguaiAmountRules.Minimum.ToString(
                    CultureInfo.InvariantCulture
                );
                propertySchema.Maximum = PaguaiAmountRules.Maximum.ToString(
                    CultureInfo.InvariantCulture
                );
            }
        }

        return Task.CompletedTask;
    }
}
