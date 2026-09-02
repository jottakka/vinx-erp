using Npgsql;
using Xunit;

namespace Vinx.Erp.PrivateQualityTests.Infrastructure;

/// <summary>
/// Utilitários de asserção dos ataques: toda negação esperada precisa se
/// manifestar como erro do Postgres (preferencialmente SQLSTATE 42501) ou como
/// zero linhas; um ataque que executa com sucesso reprova o teste.
/// </summary>
internal static class AbuseAssercoes
{
    /// <summary>Conta linhas de um SELECT dentro da transação do ataque.</summary>
    public static async Task<long> ContarLinhasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        return (long)(result ?? throw new InvalidOperationException($"A contagem '{sql}' não retornou linha."));
    }

    /// <summary>Executa o ataque e devolve a negação; reprova se o comando conseguir executar.</summary>
    public static async Task<PostgresException> EsperarNegacaoAsync(
        string connectionString,
        string sql,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException denial)
        {
            return denial;
        }

        // Assert.Fail lança; esta linha só satisfaz o compilador.
        Assert.Fail($"O ataque executou sem negação: {sql}");
        return null!;
    }

    /// <summary>Define GUCs transacionais no papel hostil, simulando a forja do contexto.</summary>
    public static async Task ForjarContextoAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        params (string Key, string Value)[] settings)
    {
        foreach (var (key, value) in settings)
        {
            await using var command = new NpgsqlCommand(
                "SELECT set_config(@key, @value, true)", connection, transaction);
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
