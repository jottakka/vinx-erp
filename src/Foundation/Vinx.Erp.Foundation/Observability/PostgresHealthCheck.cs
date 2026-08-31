using System.Data.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Observability;

/// <summary>
/// Health check "postgres": abre uma conexão real — pelo NpgsqlDataSource
/// registrado ou pela conexão do FoundationDbContext — e executa SELECT 1.
/// Mantém o readiness ancorado no banco em vez de responder vazio enquanto
/// a instância está inalcançável.
/// </summary>
public sealed class PostgresHealthCheck(
    NpgsqlDataSource? dataSource = null,
    FoundationDbContext? database = null
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (dataSource is not null)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(
                    cancellationToken
                );
                return await ProbeAsync(connection, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    "postgres unreachable.",
                    exception
                );
            }
        }

        if (database is not null)
        {
            try
            {
                await database.Database.OpenConnectionAsync(cancellationToken);
                return await ProbeAsync(
                    database.Database.GetDbConnection(),
                    cancellationToken
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new HealthCheckResult(
                    context.Registration.FailureStatus,
                    "postgres unreachable.",
                    exception
                );
            }
            finally
            {
                await database.Database.CloseConnectionAsync(CancellationToken.None);
            }
        }

        // Sem NpgsqlDataSource e sem FoundationDbContext o host não tem banco
        // wiring: o readiness falha alto em vez de fingir saúde.
        return HealthCheckResult.Unhealthy("postgres_not_configured");
    }

    private static async Task<HealthCheckResult> ProbeAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy("postgres reachable.");
    }
}
