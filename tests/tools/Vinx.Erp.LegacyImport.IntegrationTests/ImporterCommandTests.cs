using System.Text;
using Vinx.Erp.LegacyImport;
using Xunit;

namespace Vinx.Erp.LegacyImport.IntegrationTests;

/// <summary>
/// Provas do contrato de idempotência em Postgres real (Testcontainers),
/// sobre fixture sintética clientes → staging de Customers.
/// Cada teste provê seu próprio container: isolamento total entre métodos.
/// </summary>
public sealed class ImporterCommandTests
{
    private static string MappingPath =>
        Path.Combine(Path.GetTempPath(), $"legacy-import-mapping-{Guid.NewGuid():N}.json");

    private static string CompanyJson(string companyKey, string legacyEmpresaId, string targetCompanyId) => $$"""
        {
          "companyKey": "{{companyKey}}",
          "legacyEmpresaId": "{{legacyEmpresaId}}",
          "targetCompanyId": "{{targetCompanyId}}"
        }
        """;

    /// <summary>Mapeia as duas empresas sintéticas; alvo e legado são GUIDs inventados.</summary>
    private static string MappingJson(string batchId = "rehearsal-1", string[]? companies = null)
    {
        companies ??=
        [
            CompanyJson("11111111-aaaa-4111-8111-111111111111", "11111111-aaaa-4111-8111-111111111111", "9a000000-0000-4000-8000-000000000001"),
            CompanyJson("22222222-bbbb-4222-8222-222222222222", "22222222-bbbb-4222-8222-222222222222", "9a000000-0000-4000-8000-000000000002"),
        ];
        return $$"""
            {
              "sourceBatchId": "{{batchId}}",
              "companies": [
                {{string.Join(",", companies)}}
              ]
            }
            """;
    }

    [Fact]
    public async Task Idempotent_replay_skips_and_mutated_input_conflicts()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson());
        try
        {
            // Execução 1: importa tudo.
            int run1;
            string stdout1;
            string stderr1;
            using (var io = Capture())
            {
                run1 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                (stdout1, stderr1) = (io.Stdout, io.Stderr);
            }
            Assert.True(
                run1 == 0 && stdout1.Contains("imported_new"),
                $"exit={run1} stdout=[{stdout1}] stderr=[{stderr1}]");
            Assert.Equal(0, run1);
            Assert.Equal(5, await fx.ExecuteCountAsync("SELECT count(*) FROM legacy_import.customers"));
            Assert.Equal(2, await fx.ExecuteCountAsync("SELECT count(*) FROM legacy_import.import_receipts"));
            var customersAfterRun1 = await StagedFingerprint(fx);
            var receiptsAfterRun1 = await ReceiptFingerprint(fx);

            // Execução 2: replay idêntico — nada muda, reporta skip.
            int run2;
            string summary2;
            using (var io = Capture())
            {
                run2 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                summary2 = io.Stdout;
            }
            Assert.Equal(0, run2);
            Assert.Contains("replayed_skip", summary2);
            Assert.Equal(customersAfterRun1, await StagedFingerprint(fx));
            Assert.Equal(receiptsAfterRun1, await ReceiptFingerprint(fx));

            // Execução 2-b: hash de mapeamento DIFERENTE sob o mesmo batch —
            // conflito ANTES de qualquer escrita (nenhum receipt extra é criado).
            var mappingVersionRun = await new LegacyImporter().ApplyAsync(
                new LegacyImporter.Options(
                    fx.ConnectionString,
                    "rehearsal-1",
                    [
                        new CompanyMapping("11111111-aaaa-4111-8111-111111111111",
                            Guid.Parse("11111111-aaaa-4111-8111-111111111111"),
                            Guid.Parse("9a000000-0000-4000-8000-000000000001")),
                        new CompanyMapping("22222222-bbbb-4222-8222-222222222222",
                            Guid.Parse("22222222-bbbb-4222-8222-222222222222"),
                            Guid.Parse("9a000000-0000-4000-8000-000000000002")),
                    ])
                { MappingVersionHashOverride = new string('a', 64) },
                CancellationToken.None);
            Assert.False(mappingVersionRun.Succeeded);
            Assert.All(mappingVersionRun.Companies,
                c => Assert.Equal(CompanyImportOutcome.ConflictedRollback, c.Outcome));
            Assert.Equal(customersAfterRun1, await StagedFingerprint(fx));
            Assert.Equal(receiptsAfterRun1, await ReceiptFingerprint(fx));

            // Execução 3: mesmo batch, payload mutado — conflito fail-closed sem escrita parcial.
            await fx.ExecuteAsync(
                "UPDATE legacy.clientes SET nome = 'Cliente Sintetico Um ALTERADO' WHERE codigo = 'CLI-0001'");
            int run3;
            using (var io = Capture())
            {
                run3 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
            }
            Assert.NotEqual(0, run3);
            Assert.Equal(customersAfterRun1, await StagedFingerprint(fx));
            Assert.Equal(receiptsAfterRun1, await ReceiptFingerprint(fx));
        }
        finally
        {
            File.Delete(mapping);
        }
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson());
        try
        {
            int exit;
            string summary;
            string stderr4;
            using (var io = Capture())
            {
                // Modo padrão: sem --apply. Nada pode ser escrito, nem receipt.
                exit = await LegacyImportCommand.RunAsync(["--mapping", mapping]);
                (summary, stderr4) = (io.Stdout, io.Stderr);
            }

            Assert.True(exit == 0, $"exit={exit} stdout=[{summary}] stderr=[{stderr4}]");
            Assert.Contains("dry_run", summary);
            Assert.True(await fx.StagingMissingAsync());
        }
        finally
        {
            File.Delete(mapping);
        }
    }

    /// <summary>
    /// Regressão da colisão de digest: a serialização antiga ("|" + sentinela
    /// "∅") dava o MESMO SHA-256 para codigo="a|b",nome="c" e codigo="a",
    /// nome="b|c". Agora o enquadramento canônico tipado os distingue: mesmo
    /// batch conflita fail-closed e batches distintos gravam digests diferentes.
    /// </summary>
    [Fact]
    public async Task Digest_collision_regression_pipe_joined_fields_now_diverge()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        const string collidingRow = "d0000000-0000-4000-8000-000000000001";
        await fx.ExecuteAsync($"""
            INSERT INTO legacy.clientes (id, empresa_id, codigo, nome, email, ativo, criado_em) VALUES
                ('{collidingRow}', '11111111-aaaa-4111-8111-111111111111', 'a|b', 'c', NULL, true, '2026-02-01T00:00:00Z')
            """);

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson("digest-collision"));
        var mappingNewBatch = MappingPath;
        await File.WriteAllTextAsync(mappingNewBatch, MappingJson("digest-collision-2"));
        try
        {
            // Carga com o primeiro estado: ("a|b", "c").
            int run1;
            string stdout1;
            string stderr1;
            using (var io = Capture())
            {
                run1 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                (stdout1, stderr1) = (io.Stdout, io.Stderr);
            }
            Assert.True(run1 == 0 && stdout1.Contains("imported_new"), $"exit={run1} stdout=[{stdout1}] stderr=[{stderr1}]");
            var digest1 = await CompanyDigestAsync(fx, "11111111-aaaa-4111-8111-111111111111", "digest-collision");

            // Mutação colidente: mesmos campos, mesmo tamanho, outro enquadramento.
            await fx.ExecuteAsync(
                $"UPDATE legacy.clientes SET codigo = 'a', nome = 'b|c' WHERE id = '{collidingRow}'");

            // Mesmo batch ⇒ digest diferente ⇒ conflito fail-closed (antes: replayed_skip falso).
            int run2;
            string stdout2;
            using (var io = Capture())
            {
                run2 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                stdout2 = io.Stdout;
            }
            Assert.NotEqual(0, run2);
            Assert.Contains("conflicted_rollback", stdout2);

            // Batch novo sobre staging zerado: o segundo estado colidente é
            // importado e grava digest DISTINTO do primeiro estado.
            await fx.ExecuteAsync("DROP SCHEMA legacy_import CASCADE");
            int run3;
            using (var io = Capture())
            {
                run3 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mappingNewBatch]);
            }
            Assert.Equal(0, run3);
            var digest2 = await CompanyDigestAsync(fx, "11111111-aaaa-4111-8111-111111111111", "digest-collision-2");
            Assert.NotEqual(digest1, digest2);
        }
        finally
        {
            File.Delete(mapping);
            File.Delete(mappingNewBatch);
        }
    }

    /// <summary>
    /// Regressão do hash de mapeamento: ele agora cobre o mapeamento efetivo
    /// completo (companyKey/legacyEmpresaId/targetCompanyId). Trocar a empresa
    /// alvo sob o MESMO batch vira conflito fail-closed — antes era
    /// replayed_skip silencioso com a receipt apontando para o alvo antigo.
    /// </summary>
    [Fact]
    public async Task Changed_target_company_under_same_batch_conflicts()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson("mapping-change"));
        var retargeted = MappingPath;
        await File.WriteAllTextAsync(retargeted, MappingJson("mapping-change",
        [
            CompanyJson("11111111-aaaa-4111-8111-111111111111", "11111111-aaaa-4111-8111-111111111111", "9b000000-0000-4000-8000-000000000009"),
            CompanyJson("22222222-bbbb-4222-8222-222222222222", "22222222-bbbb-4222-8222-222222222222", "9a000000-0000-4000-8000-000000000002"),
        ]));
        try
        {
            int run1;
            using (var io = Capture())
            {
                run1 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
            }
            Assert.Equal(0, run1);
            var receiptsAfterRun1 = await ReceiptFingerprint(fx);

            int run2;
            string stdout2;
            using (var io = Capture())
            {
                run2 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", retargeted]);
                stdout2 = io.Stdout;
            }
            Assert.NotEqual(0, run2);
            Assert.Contains("conflicted_rollback", stdout2);
            Assert.Equal(receiptsAfterRun1, await ReceiptFingerprint(fx));
        }
        finally
        {
            File.Delete(mapping);
            File.Delete(retargeted);
        }
    }

    /// <summary>
    /// Regressão de staging: linha preexistente divergente da fonte sob batch
    /// NOVO é conflito fail-closed — antes o ON CONFLICT DO NOTHING ignorava a
    /// divergência e a empresa reportava imported_new com staging torto.
    /// </summary>
    [Fact]
    public async Task Divergent_staging_rows_fail_closed_under_new_batch()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson("staging-divergence-1"));
        var mappingNewBatch = MappingPath;
        await File.WriteAllTextAsync(mappingNewBatch, MappingJson("staging-divergence-2"));
        try
        {
            int run1;
            using (var io = Capture())
            {
                run1 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
            }
            Assert.Equal(0, run1);

            await fx.ExecuteAsync(
                "UPDATE legacy_import.customers SET legal_name = 'DIVERGIDO' WHERE code = 'CLI-0001'");

            int run2;
            string stdout2;
            using (var io = Capture())
            {
                run2 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mappingNewBatch]);
                stdout2 = io.Stdout;
            }
            Assert.NotEqual(0, run2);
            Assert.Contains("conflicted_rollback", stdout2);

            // A empresa divergente não ganhou receipt do batch novo e a linha
            // divergente permanece como estava (rollback da transação dela).
            Assert.Equal(0, await fx.ExecuteCountAsync("""
                SELECT count(*) FROM legacy_import.import_receipts
                WHERE source_batch_id = 'staging-divergence-2'
                  AND company_key = '11111111-aaaa-4111-8111-111111111111'
                """));
            Assert.Equal(1, await fx.ExecuteCountAsync(
                "SELECT count(*) FROM legacy_import.customers WHERE legal_name = 'DIVERGIDO'"));
        }
        finally
        {
            File.Delete(mapping);
            File.Delete(mappingNewBatch);
        }
    }

    /// <summary>
    /// Regressão do preflight do --apply: mapeamento incompleto (empresa
    /// legada sem linha no mapa) aborta com a MESMA validação do dry-run ANTES
    /// de qualquer DDL — nem schema de staging é criado. Antes o --apply
    /// commitava as empresas listadas e ignorava as ausentes com exit 0.
    /// </summary>
    [Fact]
    public async Task Apply_preflight_blocks_incomplete_mapping_before_any_ddl()
    {
        await using var fx = new LegacyImportPostgresFixture();
        await fx.InitializeAsync();

        var mapping = MappingPath;
        await File.WriteAllTextAsync(mapping, MappingJson("incomplete",
        [
            CompanyJson("11111111-aaaa-4111-8111-111111111111", "11111111-aaaa-4111-8111-111111111111", "9a000000-0000-4000-8000-000000000001"),
        ]));
        try
        {
            int exit;
            string stdout;
            using (var io = Capture())
            {
                exit = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                stdout = io.Stdout;
            }

            Assert.NotEqual(0, exit);
            Assert.Contains("unmapped_legacy_empresa", stdout);
            // Prova da ordem de execução: abort aconteceu antes do EnsureStaging.
            Assert.True(await fx.StagingMissingAsync());
        }
        finally
        {
            File.Delete(mapping);
        }
    }

    private static Task<string> CompanyDigestAsync(LegacyImportPostgresFixture fx, string companyKey, string batchId) =>
        fx.ExecuteScalarAsync<string>($"""
            SELECT payload_sha256 FROM legacy_import.import_receipts
            WHERE company_key = '{companyKey}' AND table_key = 'customers' AND source_batch_id = '{batchId}'
            """);

    /// <summary>Estado estável do staging para comparações entre execuções.</summary>
    private static Task<string> StagedFingerprint(LegacyImportPostgresFixture fx) =>
        fx.ExecuteScalarAsync<string>("""
            SELECT coalesce(string_agg(legacy_cliente_id::text || '|' || legal_name, E'\n' ORDER BY legacy_cliente_id), '')
            FROM legacy_import.customers
            """);

    private static Task<string> ReceiptFingerprint(LegacyImportPostgresFixture fx) =>
        fx.ExecuteScalarAsync<string>("""
            SELECT coalesce(string_agg(company_key || '|' || mapping_version_hash || '|' || payload_sha256 || '|' || applied_at::text, E'\n' ORDER BY company_key), '')
            FROM legacy_import.import_receipts
            """);

    private static ConsoleCapture Capture()
    {
        var capture = new ConsoleCapture();
        Console.SetOut(capture.BufferOut);
        Console.SetError(capture.BufferErr);
        return capture;
    }

    /// <summary>Captura stdout/stderr do CLI durante a execução; restaura no Dispose.</summary>
    private sealed class ConsoleCapture : TextWriter, IDisposable
    {
        private readonly StringWriter _bufferOut = new();
        private readonly StringWriter _bufferErr = new();
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalErr;

        public ConsoleCapture()
        {
            _originalOut = Console.Out;
            _originalErr = Console.Error;
        }

        public StringWriter BufferOut => _bufferOut;

        public StringWriter BufferErr => _bufferErr;

        public string Stdout => _bufferOut.ToString();

        public string Stderr => _bufferErr.ToString();

        public override Encoding Encoding => _bufferOut.Encoding;

        public override void Write(char value) => _bufferOut.Write(value);

        public override void Write(string? value) => _bufferOut.Write(value);

        void IDisposable.Dispose()
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalErr);
        }
    }
}
