using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Validation;

namespace Vinx.Erp.ApiHost.OpenApi;

/// <summary>Publica os limites da validação decimal no schema OpenAPI.</summary>
public sealed class DecimalRangeSchemaTransformer : IOpenApiSchemaTransformer
{
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
            var range = property
                .AttributeProvider?.GetCustomAttributes(typeof(DecimalRangeAttribute), false)
                .OfType<DecimalRangeAttribute>()
                .FirstOrDefault();
            if (range is null)
            {
                continue;
            }

            var match = schema.Properties.FirstOrDefault(entry =>
                string.Equals(entry.Key, property.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (match.Value is OpenApiSchema propertySchema)
            {
                propertySchema.Type = JsonSchemaType.String;
                propertySchema.Pattern = @"^\d+(?:\.\d{1,2})?$";
                propertySchema.Minimum = range.Minimum.ToString(CultureInfo.InvariantCulture);
                propertySchema.Maximum = range.Maximum.ToString(CultureInfo.InvariantCulture);
            }
        }

        return Task.CompletedTask;
    }
}
