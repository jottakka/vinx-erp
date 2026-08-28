using System.Text.Json;
using Npgsql;

namespace Vinx.Erp.LegacyImport;

/// <summary>
/// Importador idempotente legado → staging. Uma transação por empresa com lock
/// consultivo exclusivo; receipts decidem entre lote novo, replay e conflito
/// fail-closed. O modo dry-run valida tudo sem escrever nada (nem receipts).
/// </summary>
public sealed class LegacyImporter
{
    private const string TableKey = "customers";

    // Tabela-fonte pinada pela ledger de classificação (classification-ledger.md:
    // `clientes`, criada em schema.sql). Singular/plural é contrato, não gosto.
    private static string ClientTable(string sourceSchema) => $"\"{sourceSchema}\".clientes";
    private static readonly string[] ExpectedClientesColumns =
        ["id", "empresa_id", "codigo", "nome", "email", "ativo"];

    public sealed record Options(
        string ConnectionString,
        string SourceBatchId,
        IReadOnlyList<CompanyMapping> Companies,
        string SourceSchema = "legacy")
    {
        /// <summary>
        /// Ponto de injeção para provas de versão de mapeamento divergente
        /// (testes/ensaio); produção usa sempre o hash da convenção corrente.
        /// </summary>
        public string? MappingVersionHashOverride { get; init; }
    }

    public async Task<ApplyResult> ApplyAsync(Options options, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var results = new List<CompanyResult>();
        var succeeded = true;
        var mappingVersion = options.MappingVersionHashOverride
            ?? LegacyImportMappingFile.MappingVersionHash(options.Companies, options.SourceSchema);

        foreach (var mapping in Ordered(options.Companies))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var outcome = await ImportCompanyAsync(
                    connection, transaction, options, mapping, mappingVersion, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                results.Add(outcome);
                succeeded &= outcome.Outcome is CompanyImportOutcome.ImportedNew
                    or CompanyImportOutcome.ReplayedSkip;
            }
            catch (LegacyImportConflictException)
            {
                await transaction.RollbackAsync(cancellationToken);
                succeeded = false;
                results.Add(new CompanyResult(mapping.CompanyKey, CompanyImportOutcome.ConflictedRollback, 0));
            }
        }

        return new ApplyResult(succeeded, results);
    }

    /// <summary>
    /// Valida colunas do legado e cobertura de mapeamento, calculando digests,
    /// sem criar schema nem escrever qualquer linha. Toda a validação roda em
    /// uma única transação READ ONLY imposta pelo banco — a mesma função serve
    /// de preflight fail-closed do modo <c>--apply</c>. Problemas são
    /// retornados, nunca lançados, para o resumo processável.
    /// </summary>
    public async Task<(bool Valid, IReadOnlyList<CompanyResult> Companies, IReadOnlyList<ImportValidationProblem> Problems)> DryRunAsync(
        Options options, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // READ ONLY imposto pelo servidor: nenhuma escrita escapa da validação,
        // nem por acidente de SQL futuro.
        await using var validationTransaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var enforceReadOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, validationTransaction))
        {
            await enforceReadOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        var problems = new List<ImportValidationProblem>();

        var foundColumns = new List<string>();
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = @source_schema AND table_name = 'clientes'
                """,
                connection, validationTransaction);
            command.Parameters.AddWithValue("@source_schema", options.SourceSchema);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foundColumns.Add(reader.GetString(0));
            }
        }

        var missingColumns = ExpectedClientesColumns.Except(foundColumns).ToList();
        if (missingColumns.Count > 0)
        {
            problems.Add(new ImportValidationProblem(
                "missing_legacy_columns",
                $"colunas ausentes em {options.SourceSchema}.clientes: {string.Join(", ", missingColumns)}"));
        }

        // Empresas presentes no legado sem linha no mapa bloqueiam a importação.
        var mappedEmpresas = Ordered(options.Companies).Select(m => m.LegacyEmpresaId.ToString()).ToHashSet();
        var legacyEmpresas = new List<string>();
        {
            await using var command = new NpgsqlCommand(
                $"SELECT DISTINCT empresa_id::text FROM {ClientTable(options.SourceSchema)} ORDER BY 1",
                connection, validationTransaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                legacyEmpresas.Add(reader.GetString(0));
            }
        }

        foreach (var empresaId in legacyEmpresas.Where(e => !mappedEmpresas.Contains(e)).OrderBy(e => e))
        {
            problems.Add(new ImportValidationProblem(
                "unmapped_legacy_empresa",
                $"empresa legada sem mapeamento: {empresaId}"));
        }

        var results = new List<CompanyResult>();
        foreach (var mapping in Ordered(options.Companies))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowCount = await CountClientRowsAsync(
                connection, validationTransaction, options.SourceSchema, mapping.LegacyEmpresaId, cancellationToken);
            results.Add(new CompanyResult(mapping.CompanyKey, CompanyImportOutcome.Validated, rowCount));
        }

        await validationTransaction.RollbackAsync(cancellationToken);

        return (problems.Count == 0, results, problems);
    }

    private static async Task<CompanyResult> ImportCompanyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Options options,
        CompanyMapping mapping,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        // Exclusão concorrente: quem não pega o lock simplesmente pula a empresa.
        bool lockAcquired;
        await using (var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(hashtext(@company_key))",
            connection, transaction))
        {
            command.Parameters.AddWithValue("@company_key", mapping.CompanyKey);
            lockAcquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        if (!lockAcquired)
        {
            return new CompanyResult(mapping.CompanyKey, CompanyImportOutcome.ConcurrentSkip, 0);
        }

        // Conflito de VERSÃO de mapeamento sob o mesmo lote é bloqueado ANTES de
        // qualquer escrita: receipts com hash diferente na mesma chave falham fechado.
        await GuardAgainstDivergentMappingVersionAsync(
            connection, transaction, options.SourceBatchId, mapping.CompanyKey, mappingVersion, cancellationToken);

        var rows = await ReadClientRowsAsync(connection, transaction, options.SourceSchema, mapping.LegacyEmpresaId, cancellationToken);
        var payloadSha256 = ComputeDigest(rows);
        var stagedRows = await StageCustomersAsync(
            connection, transaction, mapping, rows, cancellationToken);

        var receiptOutcome = await WriteReceiptAndClassifyAsync(
            connection, transaction, options.SourceBatchId, mapping.CompanyKey, payloadSha256, rows.Count, mappingVersion, cancellationToken);
        return new CompanyResult(mapping.CompanyKey, receiptOutcome, stagedRows);
    }

    /// <summary>
    /// Verificação prévia: já existe receipt deste lote com OUTRA versão de
    /// mapeamento ⇒ exceção de conflito reverte a transação inteira da empresa.
    /// </summary>
    private static async Task GuardAgainstDivergentMappingVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceBatchId,
        string companyKey,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        long divergent;
        await using (var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM legacy_import.import_receipts
            WHERE company_key = @company_key AND table_key = @table_key
              AND source_batch_id = @source_batch_id
              AND mapping_version_hash <> @mapping_version_hash
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("@company_key", companyKey);
            command.Parameters.AddWithValue("@table_key", TableKey);
            command.Parameters.AddWithValue("@source_batch_id", sourceBatchId);
            command.Parameters.AddWithValue("@mapping_version_hash", mappingVersion);
            divergent = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        if (divergent > 0)
        {
            throw new LegacyImportConflictException(
                $"versão de mapeamento alterada sob o mesmo batch para a empresa {companyKey}");
        }
    }

    private sealed record ClientRow(string IdText, string? Code, string Name, string? Email, bool Active);

    private static async Task<List<ClientRow>> ReadClientRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sourceSchema, Guid empresaId, CancellationToken cancellationToken)
    {
        var rows = new List<ClientRow>();
        await using var command = new NpgsqlCommand(
            """
            SELECT id::text, codigo, nome, email, ativo
            FROM {0} WHERE empresa_id = @empresa_id ORDER BY id
            """.Replace("{0}", ClientTable(sourceSchema)),
            connection, transaction);
        command.Parameters.AddWithValue("@empresa_id", empresaId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClientRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }

        return rows;
    }

    private static async Task<int> CountClientRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sourceSchema, Guid empresaId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {ClientTable(sourceSchema)} WHERE empresa_id = @empresa_id",
            connection, transaction);
        command.Parameters.AddWithValue("@empresa_id", empresaId);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Linha de staging esperada/observada. Inclui o source_payload CANONIZADO:
    /// provenance bruta (ex.: <c>codigo</c> null vs "") decide conflito mesmo
    /// quando as colunas projetadas coincidem.
    /// </summary>
    private sealed record StagedCustomer(
        Guid LegacyClienteId,
        Guid TargetCompanyId,
        string CompanyKey,
        string Code,
        string LegalName,
        string? Email,
        bool Active,
        string SourcePayloadCanonical);

    /// <summary>Conjunto COMPLETO já em staging para a empresa (por legacy_cliente_id).</summary>
    private static async Task<Dictionary<Guid, StagedCustomer>> ReadStagedCustomersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string companyKey,
        CancellationToken cancellationToken)
    {
        var staged = new Dictionary<Guid, StagedCustomer>();
        await using var command = new NpgsqlCommand(
            """
            SELECT legacy_cliente_id, target_company_id, company_key, code, legal_name, email, active, source_payload::text
            FROM legacy_import.customers WHERE company_key = @company_key
            """,
            connection, transaction);
        command.Parameters.AddWithValue("@company_key", companyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var legacyClienteId = reader.GetGuid(0);
            staged[legacyClienteId] = new StagedCustomer(
                legacyClienteId,
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                CanonicalizeJsonText(reader.GetString(7)));
        }

        return staged;
    }

    /// <summary>Provenance bruta determinística da linha-fonte (mesma forma gravada em staging).</summary>
    private static string BuildSourcePayload(ClientRow row, CompanyMapping mapping) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["legacy_cliente_id"] = row.IdText,
            ["legacy_empresa_id"] = mapping.LegacyEmpresaId.ToString(),
            ["codigo"] = row.Code,
            ["nome"] = row.Name,
            ["email"] = row.Email,
            ["ativo"] = row.Active,
        });

    /// <summary>
    /// Reconciliação BIDIRECIONAL do conjunto completo por empresa: o staged da
    /// empresa é comparado INTEIRO contra o esperado da fonte — extra (linha
    /// cuja fonte sumiu entre batches), ausente ou divergente (colunas
    /// projetadas OU payload bruto) é conflito fail-closed. Nunca resta linha
    /// órfã em staging sob receipt nova, nem falso imported_new.
    /// </summary>
    private static async Task<int> StageCustomersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyMapping mapping,
        IReadOnlyList<ClientRow> rows,
        CancellationToken cancellationToken)
    {
        var stagedByLegacyId = await ReadStagedCustomersAsync(
            connection, transaction, mapping.CompanyKey, cancellationToken);
        var staged = 0;
        foreach (var row in rows)
        {
            var legacyClienteId = Guid.Parse(row.IdText);
            var payload = BuildSourcePayload(row, mapping);
            var expected = new StagedCustomer(
                legacyClienteId,
                mapping.TargetCompanyId,
                mapping.CompanyKey,
                row.Code ?? string.Empty,
                row.Name,
                row.Email,
                row.Active,
                CanonicalizeJsonText(payload));

            // Fail-closed em staging: linha preexistente é comparada campo a
            // campo, inclusive o payload bruto. Divergência (staging velho/
            // torto sob receipt nova) é conflito, nunca aceitação silenciosa.
            if (stagedByLegacyId.Remove(legacyClienteId, out var existing))
            {
                if (!existing.Equals(expected))
                {
                    throw new LegacyImportConflictException(
                        $"staging divergente da fonte para a empresa {mapping.CompanyKey} (cliente legado {row.IdText})");
                }

                continue;
            }

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO legacy_import.customers
                    (legacy_cliente_id, target_company_id, company_key, code, legal_name, email, active, source_payload)
                VALUES (@legacy_cliente_id, @target_company_id, @company_key, @code, @legal_name, @email, @active, @source_payload::jsonb)
                """,
                connection, transaction);
            command.Parameters.AddWithValue("@legacy_cliente_id", legacyClienteId);
            command.Parameters.AddWithValue("@target_company_id", mapping.TargetCompanyId);
            command.Parameters.AddWithValue("@company_key", mapping.CompanyKey);
            command.Parameters.AddWithValue("@code", row.Code ?? string.Empty);
            command.Parameters.AddWithValue("@legal_name", row.Name);
            command.Parameters.AddWithValue("@email", (object?)row.Email ?? DBNull.Value);
            command.Parameters.AddWithValue("@active", row.Active);
            command.Parameters.AddWithValue("@source_payload", payload);
            staged += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Sobrou staged sem fonte correspondente: a fonte MUDOU entre batches
        // (linha removida) — receipt nova sobre conjunto incompleto não existe.
        if (stagedByLegacyId.Count > 0)
        {
            var orphans = string.Join(", ", stagedByLegacyId.Keys.OrderBy(id => id.ToString()));
            throw new LegacyImportConflictException(
                $"staging contem linhas ausentes da fonte para a empresa {mapping.CompanyKey} (clientes legados {orphans})");
        }

        return staged;
    }

    /// <summary>
    /// Comparação semântica de JSON de provenance: chaves ordenadas
    /// recursivamente e strings re-serializadas pela MESMA codificação. O jsonb
    /// do Postgres reordena chaves e desenrola escapes unicode; o
    /// JsonSerializer escreve em ordem de inserção com escapes. A forma
    /// canônica garante que só diferenças SEMÂNTICAS (ex.: <c>null</c> vs "")
    /// divergem, nunca a representação.
    /// </summary>
    private static string CanonicalizeJsonText(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CanonicalJson(document.RootElement);
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(
            ",",
            element.EnumerateObject()
                .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))
                .OrderBy(member => member, StringComparer.Ordinal)) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        _ => element.GetRawText(),
    };

    /// <summary>
    /// Insere a receipt com ON CONFLICT DO NOTHING: inserida ⇒ lote novo; conflito
    /// ⇒ compara hash+digest guardados: iguais é replay válido, diferentes é
    /// conflito fail-closed (entrada mutada sob o mesmo batch).
    /// </summary>
    private static async Task<CompanyImportOutcome> WriteReceiptAndClassifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceBatchId,
        string companyKey,
        string payloadSha256,
        int rowCount,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        long? insertedReceipt;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO legacy_import.import_receipts
                (company_key, table_key, source_batch_id, mapping_version_hash, payload_sha256, row_count)
            VALUES (@company_key, @table_key, @source_batch_id, @mapping_version_hash, @payload_sha256, @row_count)
            ON CONFLICT (company_key, table_key, source_batch_id, mapping_version_hash) DO NOTHING
            RETURNING receipt_id
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("@company_key", companyKey);
            command.Parameters.AddWithValue("@table_key", TableKey);
            command.Parameters.AddWithValue("@source_batch_id", sourceBatchId);
            command.Parameters.AddWithValue("@mapping_version_hash", mappingVersion);
            command.Parameters.AddWithValue("@payload_sha256", payloadSha256);
            command.Parameters.AddWithValue("@row_count", rowCount);
            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            insertedReceipt = scalar is null ? null : (long)scalar;
        }

        if (insertedReceipt is not null)
        {
            return CompanyImportOutcome.ImportedNew;
        }

        string? storedHash;
        string? storedDigest;
        await using (var command = new NpgsqlCommand(
            """
            SELECT mapping_version_hash, payload_sha256
            FROM legacy_import.import_receipts
            WHERE company_key = @company_key AND table_key = @table_key
              AND source_batch_id = @source_batch_id AND mapping_version_hash = @mapping_version_hash
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("@company_key", companyKey);
            command.Parameters.AddWithValue("@table_key", TableKey);
            command.Parameters.AddWithValue("@source_batch_id", sourceBatchId);
            command.Parameters.AddWithValue("@mapping_version_hash", mappingVersion);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new LegacyImportConflictException(
                    $"receipt conflitante sem registro correspondente para empresa {companyKey}");
            }

            storedHash = reader.GetString(0);
            storedDigest = reader.GetString(1);
        }

        if (storedHash == mappingVersion && storedDigest == payloadSha256)
        {
            return CompanyImportOutcome.ReplayedSkip;
        }

        throw new LegacyImportConflictException(
            $"lote mutado sob o mesmo batch para a empresa {companyKey}: hash/digest divergentes");
    }

    /// <summary>
    /// Digest do lote sobre serialização canônica tipada e com prefixo de
    /// comprimento: ("a|b","c") e ("a","b|c") — e null vs "∅" — são bytes de
    /// enquadramento distintos, logo SHA-256 diferente. A antiga junção por
    /// "|" com sentinela "∅" colidia e era explorável para replay de lote mutado.
    /// </summary>
    private static string ComputeDigest(IReadOnlyList<ClientRow> rows)
    {
        using var canonical = new CanonicalDigest.Builder("vinx-legacy-import/payload/v2");
        canonical.WriteLong(rows.Count);
        foreach (var row in rows)
        {
            canonical.WriteNullableString(row.IdText)
                .WriteNullableString(row.Code)
                .WriteNullableString(row.Name)
                .WriteNullableString(row.Email)
                .WriteBool(row.Active);
        }

        return canonical.ToSha256Hex();
    }

    private static IEnumerable<CompanyMapping> Ordered(IReadOnlyList<CompanyMapping> companies) =>
        companies.OrderBy(m => m.CompanyKey, StringComparer.Ordinal);
}
