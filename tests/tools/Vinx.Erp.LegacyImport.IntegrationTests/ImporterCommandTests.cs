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

            // Execução 3: mesmo batch, payload mutado — conflito fail-closed sem escrita parcial.
            await fx.ExecuteAsync(
                "UPDATE legacy.cliente SET nome = 'Cliente Sintetico Um ALTERADO' WHERE codigo = 'CLI-0001'");
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
