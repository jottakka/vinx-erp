using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Carregador do arquivo de mapeamento (JSON simples, sem DSL): batch + empresas.
/// Qualquer campo ausente/inválido é erro de configuração, não inferência.
/// </summary>
internal static class LegacyImportMappingFile
{
    public sealed record Parsed(
        string SourceBatchId,
        string SourceSchema,
        IReadOnlyList<CompanyMapping> Companies);

    public static Parsed Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var sourceBatchId = root.TryGetProperty("sourceBatchId", out var batchProperty)
            && batchProperty.ValueKind == JsonValueKind.String
            ? batchProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(sourceBatchId))
        {
            throw new FormatException("mapping: sourceBatchId é obrigatório");
        }

        if (!root.TryGetProperty("companies", out var companiesProperty)
            || companiesProperty.ValueKind != JsonValueKind.Array
            || companiesProperty.GetArrayLength() == 0)
        {
            throw new FormatException("mapping: companies deve ser uma lista não vazia");
        }

        var sourceSchema = root.TryGetProperty("sourceSchema", out var schemaProperty)
            && schemaProperty.ValueKind == JsonValueKind.String
            ? schemaProperty.GetString()
            : null;
        if (sourceSchema is not null && !Regex.IsMatch(sourceSchema, "^[a-z_][a-z_0-9]*$"))
        {
            throw new FormatException("mapping: sourceSchema inválido (use [a-z_][a-z_0-9]*)");
        }

        var companies = new List<CompanyMapping>();
        foreach (var entry in companiesProperty.EnumerateArray())
        {
            companies.Add(new CompanyMapping(
                CompanyKey: RequiredString(entry, "companyKey"),
                LegacyEmpresaId: Guid.Parse(RequiredString(entry, "legacyEmpresaId")),
                TargetCompanyId: Guid.Parse(RequiredString(entry, "targetCompanyId"))));
        }

        return new Parsed(sourceBatchId!, sourceSchema ?? "legacy", companies);
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new FormatException($"mapping: {name} é obrigatório");

    /// <summary>
    /// Hash determinístico da versão de mapeamento: SHA-256 sobre a convenção
    /// (tabela, colunas e versão do DDL de staging) E o mapeamento efetivo
    /// completo — schema-fonte e cada par companyKey/legacyEmpresaId/
    /// targetCompanyId — em serialização canônica tipada. Qualquer mudança
    /// (trocar a empresa alvo sob o mesmo batch, por exemplo) muda o hash e
    /// força conflito fail-closed com receipts antigas, conforme
    /// docs/migration/ledgers/identity-mapping.md ("o hash cobre a ledger").
    /// </summary>
    public static string MappingVersionHash(IReadOnlyList<CompanyMapping> companies, string sourceSchema)
    {
        const string convention =
            "vinx-legacy-import/v2|table_key=customers|source_table=clientes"
            + "|staging_ddl=1|columns=legacy_cliente_id,target_company_id,company_key,code,legal_name,email,active";
        using var canonical = new CanonicalDigest.Builder("vinx-legacy-import/mapping-version/v2");
        canonical.WriteNullableString(convention)
            .WriteNullableString(sourceSchema)
            .WriteLong(companies.Count);
        foreach (var company in companies.OrderBy(c => c.CompanyKey, StringComparer.Ordinal))
        {
            canonical.WriteNullableString(company.CompanyKey)
                .WriteNullableString(company.LegacyEmpresaId.ToString())
                .WriteNullableString(company.TargetCompanyId.ToString());
        }

        return canonical.ToSha256Hex();
    }
}
