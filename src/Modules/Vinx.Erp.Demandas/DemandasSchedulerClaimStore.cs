using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Demandas;

/// <summary>
/// Resultado de uma tentativa de claims/renew contra a linha única de
/// identidade do scheduler em <c>demandas.scheduler_claim</c>.
/// </summary>
public sealed record DemandasSchedulerClaimOutcome(bool Won, long Generation);

/// <summary>
/// Identidade durável cluster-wide do scheduler de automação de Demandas
/// (revisão item 4): UMA linha de claim por nome de cadeia; a geração cerca o
/// fence — cada boot vencedor incrementa a geração e só a cadeia portadora da
/// geração vigente renova o lease e perpetua o próximo tick. Cadeias órfãs de
/// geração antiga morrem sozinhas ao não renovar, garantindo um único
/// encadeamento vivo mesmo com restarts e réplicas concorrentes.
/// </summary>
public sealed class DemandasSchedulerClaimStore(FoundationDbContext database)
{
    /// <summary>Nome da cadeia única da automação de demandas.</summary>
    public const string ChainName = "demandas-automation";

    /// <summary>
    /// Tenta assumir a cadeia com uma geração nova: vence apenas quando a linha
    /// não existe ou o lease do dono atual venceu (CAS no UPDATE condicional).
    /// </summary>
    public async Task<DemandasSchedulerClaimOutcome> TryClaimAsync(
        string ownerHost,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        var leaseSeconds = Math.Max(1, (int)lease.TotalSeconds);
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        if (database.Database.CurrentTransaction is { } current)
        {
            command.Transaction = current.GetDbTransaction();
        }

        command.CommandText = """
            INSERT INTO demandas.scheduler_claim AS claim (name, generation, owner_host, lease_expires_at, updated_at)
            VALUES (@name, 1, @owner_host, now() + make_interval(secs => @lease_seconds), now())
            ON CONFLICT (name) DO UPDATE
                SET generation = claim.generation + 1,
                    owner_host = EXCLUDED.owner_host,
                    lease_expires_at = now() + make_interval(secs => @lease_seconds),
                    updated_at = now()
                WHERE claim.lease_expires_at <= now()
            RETURNING generation
            """;
        ((NpgsqlCommand)command).Parameters.AddWithValue("name", ChainName);
        ((NpgsqlCommand)command).Parameters.AddWithValue("owner_host", ownerHost);
        ((NpgsqlCommand)command).Parameters.AddWithValue("lease_seconds", leaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(false, 0);
        }

        return new(true, reader.GetInt64(0));
    }

    /// <summary>
    /// Renova o lease da geração corrente: só a cadeia com a geração certa (e
    /// lease ainda vivo) renova — geração superseded perde e encerra a cadeia.
    /// </summary>
    public async Task<DemandasSchedulerClaimOutcome> TryRenewAsync(
        long generation,
        string ownerHost,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        var leaseSeconds = Math.Max(1, (int)lease.TotalSeconds);
        var renewed = await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE demandas.scheduler_claim
            SET lease_expires_at = now() + make_interval(secs => {leaseSeconds}),
                owner_host = {ownerHost},
                updated_at = now()
            WHERE name = {ChainName}
              AND generation = {generation}
              AND lease_expires_at > now()
            """, cancellationToken);
        return renewed == 1 ? new(true, generation) : new(false, generation);
    }
}
