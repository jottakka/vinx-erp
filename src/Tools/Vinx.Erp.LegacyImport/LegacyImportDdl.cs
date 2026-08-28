namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Convenções DDL do schema de staging. Versões são explícitas porque entram
/// no hash de mapeamento: mudar aqui invalida receipts antigas de propósito.
/// </summary>
internal static class LegacyImportDdl
{
    public const string StagingSchemaName = "legacy_import";

    /// <summary>Criação idempotente do schema/staging/receipts (somente modo --apply).</summary>
    public const string EnsureStaging = """
        CREATE SCHEMA IF NOT EXISTS legacy_import;

        CREATE TABLE IF NOT EXISTS legacy_import.customers (
            legacy_cliente_id uuid PRIMARY KEY,
            target_company_id uuid NOT NULL,
            company_key text NOT NULL,
            code text NOT NULL,
            legal_name text NOT NULL,
            email text,
            active boolean NOT NULL,
            source_payload jsonb NOT NULL,
            imported_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS legacy_import.import_receipts (
            receipt_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            company_key text NOT NULL,
            table_key text NOT NULL,
            source_batch_id text NOT NULL,
            mapping_version_hash text NOT NULL,
            payload_sha256 text NOT NULL,
            row_count integer NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_import_receipts
                UNIQUE (company_key, table_key, source_batch_id, mapping_version_hash)
        );
        """;
}
