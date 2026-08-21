using System.Text.Json;

namespace Vinx.Erp.ApiHost.Contracts;

/// <summary>
/// Leitura estrita e compartilhada do corpo JSON dos endpoints minimal do
/// ApiHost: o corpo precisa ser objeto, cada propriedade precisa pertencer ao
/// contrato e nenhuma pode repetir. O modo exato exige ainda a presença de
/// todos os campos. Mantém a semântica dos contratos anteriores eliminando o
/// scaffolding de parse duplicado por endpoint.
/// </summary>
internal static class StrictJsonFieldSet
{
    /// <summary>
    /// Exige corpo-objeto e propriedades únicas dentro do contrato; campos
    /// ausentes ficam a critério do chamador (contratos com campo opcional).
    /// </summary>
    public static bool TryReadObject(
        JsonElement body,
        IReadOnlySet<string> fields,
        out Dictionary<string, JsonElement> values)
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in body.EnumerateObject())
        {
            if (!fields.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Além das garantias de <see cref="TryReadObject"/>, exige que todos os
    /// campos do contrato estejam presentes (contratos sem campo opcional).
    /// </summary>
    public static bool TryReadExact(
        JsonElement body,
        IReadOnlySet<string> fields,
        out Dictionary<string, JsonElement> values) =>
        TryReadObject(body, fields, out values) && values.Count == fields.Count;
}
