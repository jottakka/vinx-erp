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

    /// <summary>Mapeia as duas empresas sintéticas; alvo e legado são GUIDs inventados.</summary>
    private static string MappingJson(string batchId = "rehearsal-1") => $$"""
        {
          "sourceBatchId": "{{batchId}}",
          "companies": [
            {
              "companyKey": "11111111-aaaa-4111-8111-111111111111",
              "legacyEmpresaId": "11111111-aaaa-4111-8111-111111111111",
              "targetCompanyId": "9a000000-0000-4000-8000-000000000001"
            },
            {
              "companyKey": "22222222-bbbb-4222-8222-222222222222",
              "legacyEmpresaId": "22222222-bbbb-4222-8222-222222222222",
              "targetCompanyId": "9a000000-0000-4000-8000-000000000002"
            }
          ]
        }
        """;

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
            var sink1 = Capture();
            try
            {
                run1 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                Assert.Contains("imported_new", sink1.Text);
            }
            finally
            {
                sink1.DisposeSink();
            }
            Assert.Equal(0, run1);
            Assert.Equal(5, await fx.ExecuteCountAsync("SELECT count(*) FROM legacy_import.customers"));
            Assert.Equal(2, await fx.ExecuteCountAsync("SELECT count(*) FROM legacy_import.import_receipts"));
            var customersAfterRun1 = await StagedFingerprint(fx);
            var receiptsAfterRun1 = await ReceiptFingerprint(fx);

            // Execução 2: replay idêntico — nada muda, reporta skip.
            int run2;
            string summary2;
            var sink2 = Capture();
            try
            {
                run2 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
                summary2 = sink2.Text;
            }
            finally
            {
                sink2.DisposeSink();
            }
            Assert.Equal(0, run2);
            Assert.Contains("replayed_skip", summary2);
            Assert.Equal(customersAfterRun1, await StagedFingerprint(fx));
            Assert.Equal(receiptsAfterRun1, await ReceiptFingerprint(fx));

            // Execução 3: mesmo batch, payload mutado — conflito fail-closed sem escrita parcial.
            await fx.ExecuteAsync(
                "UPDATE legacy.cliente SET nome = 'Cliente Sintetico Um ALTERADO' WHERE codigo = 'CLI-0001'");
            int run3;
            var sink3 = Capture();
            try
            {
                run3 = await LegacyImportCommand.RunAsync(["--apply", "--mapping", mapping]);
            }
            finally
            {
                sink3.DisposeSink();
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
            var sink = Capture();
            try
            {
                // Modo padrão: sem --apply. Nada pode ser escrito, nem receipt.
                exit = await LegacyImportCommand.RunAsync(["--mapping", mapping]);
                summary = sink.Text;
            }
            finally
            {
                sink.DisposeSink();
            }

            Assert.Equal(0, exit);
            Assert.Contains("dry_run", summary);
            Assert.True(await fx.StagingMissingAsync());
        }
        finally
        {
            File.Delete(mapping);
        }
    }

    /// <summary>Estado estável do staging para comparações entre execuções.</summary>
    private static Task<string> StagedFingerprint(LegacyImportPostgresFixture fx) =>
        fx.ExecuteScalarAsync<string>("""
            SELECT coalesce(string_agg(legacy_cliente_id || '|' || legal_name ORDER BY legacy_cliente_id), '')
            FROM legacy_import.customers
            """);

    private static Task<string> ReceiptFingerprint(LegacyImportPostgresFixture fx) =>
        fx.ExecuteScalarAsync<string>("""
            SELECT coalesce(string_agg(company_key || '|' || mapping_version_hash || '|' || payload_sha256 || '|' || applied_at::text ORDER BY company_key), '')
            FROM legacy_import.import_receipts
            """);

    private static ConsoleSink Capture()
    {
        var sink = new ConsoleSink();
        Console.SetOut(sink);
        return sink;
    }

    /// <summary>Captura o stdout do CLI durante a execução; restaura no Dispose.</summary>
    private sealed class ConsoleSink : TextWriter
    {
        private readonly StringWriter _buffer = new();
        private readonly TextWriter _original = Console.Out;

        public override Encoding Encoding => _buffer.Encoding;

        public override void Write(char value) => _buffer.Write(value);

        public override void Write(string? value) => _buffer.Write(value);

        public string Text => _buffer.ToString();

        public void DisposeSink() => Console.SetOut(_original);
    }
}
