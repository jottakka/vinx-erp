using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Platform;

public sealed record PlatformOperatorBootstrapCommand(
    string Subject,
    string Issuer,
    string DisplayName,
    string ProviderEmail,
    Guid CorrelationId
);

public enum PlatformOperatorBootstrapState
{
    Created,
    AlreadyConfigured,
    Conflict,
    Unavailable,
}

/// <summary>
/// Executa bootstrap de operador por função exclusiva do contexto migrador.
/// </summary>
public sealed class PlatformOperatorBootstrapService(
    FoundationDbContext database,
    ILogger<PlatformOperatorBootstrapService> logger
)
{
    private const string Operation = "platform_operator_bootstrap";
    private const string Module = "foundation";
    private const string EventTemplate =
        "Platform operator bootstrap {Operation} {Module} {Result} {Reason}";

    /// <summary>
    /// Executa somente função de banco; verificação externa pertence ao host chamador.
    /// </summary>
    public async Task<PlatformOperatorBootstrapState> ExecuteAsync(
        PlatformOperatorBootstrapCommand command,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var status = await database
                .Database.SqlQuery<string>(
                    $"""
                    SELECT status AS "Value"
                    FROM foundation.bootstrap_platform_operator(
                        {command.Subject},
                        {command.Issuer},
                        {command.DisplayName},
                        {command.ProviderEmail},
                        {command.CorrelationId})
                    """
                )
                .SingleOrDefaultAsync(cancellationToken);
            return status switch
            {
                "created" => PlatformOperatorBootstrapState.Created,
                "already_configured" => PlatformOperatorBootstrapState.AlreadyConfigured,
                "conflict" => PlatformOperatorBootstrapState.Conflict,
                _ => LogUnavailable(),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return LogUnavailable(exception);
        }
    }

    private PlatformOperatorBootstrapState LogUnavailable(Exception? exception = null)
    {
        logger.LogError(
            exception,
            EventTemplate,
            Operation,
            Module,
            "unavailable",
            "bootstrap_execution_failed"
        );
        return PlatformOperatorBootstrapState.Unavailable;
    }
}
