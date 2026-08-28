using Npgsql;
using Testcontainers.PostgreSql;

namespace Vinx.Erp.LegacyImport.IntegrationTests;

/// <summary>
/// Container Postgres efêmero com o subset sintético do legado (schema `legacy`).
/// Os dados são inventados e determinísticos; nenhum dado de cliente entra aqui.
/// </summary>
public sealed class LegacyImportPostgresFixture : IAsyncDisposable
{
    private const string ConnectionEnvironmentVariable = "LEGACY_IMPORT_CONNECTION";

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17.9-alpine").Build();

    private string? _previousConnectionString;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        // O CLI lê a conexão do ambiente para nunca expô-la em argumentos de processo.
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Environment.SetEnvironmentVariable(ConnectionEnvironmentVariable, ConnectionString);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(SyntheticLegacy.SchemaAndSeed, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ExecuteCountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Somatório de nulos nas colunas-chave do alvo para provar ausência.</summary>
    public Task<bool> StagingMissingAsync() =>
        ExecuteScalarAsync<bool>("SELECT to_regclass('legacy_import.customers') IS NULL AND to_regclass('legacy_import.import_receipts') IS NULL");

    public async Task<T> ExecuteScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ConnectionEnvironmentVariable, _previousConnectionString);
        await _container.DisposeAsync();
    }
}

internal static class SyntheticLegacy
{
    // Empresas sintéticas: Matriz Alfa Ltda e Filo Beta SA (dados de teste).
    internal const string SchemaAndSeed = """
        CREATE SCHEMA legacy;
        CREATE TABLE legacy.empresa (
            id uuid PRIMARY KEY,
            nome text NOT NULL
        );
        -- Tabela-fonte espelha o contrato pinado em docs/migration/ledgers/
        -- classification-ledger.md: `clientes` (plural), nunca singular.
        CREATE TABLE legacy.clientes (
            id uuid PRIMARY KEY,
            empresa_id uuid NOT NULL REFERENCES legacy.empresa(id),
            codigo text,
            nome text NOT NULL,
            email text,
            ativo boolean NOT NULL DEFAULT true,
            criado_em timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO legacy.empresa (id, nome) VALUES
            ('11111111-aaaa-4111-8111-111111111111', 'Empresa Sintetica Um'),
            ('22222222-bbbb-4222-8222-222222222222', 'Empresa Sintetica Dois');
        INSERT INTO legacy.clientes (id, empresa_id, codigo, nome, email, ativo, criado_em) VALUES
            ('c0000000-0000-4000-8000-000000000001', '11111111-aaaa-4111-8111-111111111111', 'CLI-0001', 'Cliente Sintetico Um', 'um@example.test', true, '2026-01-10T12:00:00Z'),
            ('c0000000-0000-4000-8000-000000000002', '11111111-aaaa-4111-8111-111111111111', 'CLI-0002', 'Cliente Sintetico Dois', NULL, true, '2026-01-11T09:30:00Z'),
            ('c0000000-0000-4000-8000-000000000003', '11111111-aaaa-4111-8111-111111111111', 'CLI-0003', 'Cliente Sintetico Tres', 'tres@example.test', false, '2026-01-12T08:15:00Z'),
            ('c0000000-0000-4000-8000-000000000004', '22222222-bbbb-4222-8222-222222222222', 'CLI-0004', 'Cliente Sintetico Quatro', NULL, true, '2026-01-13T17:45:00Z'),
            ('c0000000-0000-4000-8000-000000000005', '22222222-bbbb-4222-8222-222222222222', 'CLI-0005', 'Cliente Sintetico Cinco', 'cinco@example.test', false, '2026-01-14T10:05:00Z');
        """;
}
