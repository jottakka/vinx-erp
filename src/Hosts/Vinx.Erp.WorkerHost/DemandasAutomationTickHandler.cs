using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Demandas;
using Wolverine;


namespace Vinx.Erp.WorkerHost;

/// <summary>
/// Mensagem de tick da automação de demandas (fila durável local). A geração
/// cerca o fence da identidade durável: apenas a cadeia portadora da geração
/// vigente do claim renova o lease e perpetua o próximo tick.
///
/// MUDANÇA DE TIPO BLOQUEADA (#379): o nome CLR deste record é a identidade
/// durável dos envelopes Wolverine já persistidos — movê-lo para
/// Vinx.Erp.Demandas quebra a desserialização da fila em deploy. O move
/// exige upcaster/prova de upgrade com envelope pré-deploy.
/// </summary>
public sealed record DemandasAutomationTick(DateTimeOffset ScheduledAt, long Generation);

/// <summary>
/// Handler do tick: executa a varredura (avança DUE primeiro, depois overdue) e
/// agenda o próximo tick SOMENTE se a renovação do claim durável confirmar que
/// esta geração ainda é a dona da cadeia. Classes públicas com inline-DI —
/// handler interno/lambda é engolido silenciosamente como Handled pela cadeia
/// Wolverine. O contrato <see cref="DemandasAutomationTick"/> vive no módulo
/// Demandas (fila <see cref="DemandasQueues.Automation"/>, nome canônico).
/// </summary>
public sealed class DemandasAutomationTickHandler(
    DemandasAutomationScanService scan,
    DemandasSchedulerClaimStore claims,
    IMessageBus messageBus,
    ILogger<DemandasAutomationTickHandler> logger
)
{
    private const string Operation = "demandas_automation_tick";
    private const string Module = "worker";

    public async Task Handle(DemandasAutomationTick message, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await scan.ScanAsync(cancellationToken);
            logger.LogInformation(
                "Demandas {Operation} {Module} {Result} {Companies} {Advanced} {Overdue}",
                Operation,
                Module,
                "succeeded",
                summary.CompaniesScanned,
                summary.StagesAdvanced,
                summary.DemandsMarkedOverdue
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Tick único falho não derruba a cadeia: loga e continua agendando.
            logger.LogError(
                exception,
                "Demandas {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "unavailable",
                "tick_failed"
            );
        }

        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var renewal = await claims.TryRenewAsync(message.Generation, owner, TimeSpan.FromTicks(3 * DemandasScheduler.Interval.Ticks), cancellationToken);
        if (!renewal.Won)
        {
            // Geração superseded (restart/replica assumiu ou lease venceu): a
            // cadeia antiga morre aqui — o novo dono semeia a sua própria.
            logger.LogInformation(
                "Demandas {Operation} {Module} {Result} {Reason} {Generation}",
                Operation,
                Module,
                "superseded",
                "chain_claim_lost",
                message.Generation
            );
            return;
        }

        await messageBus.ScheduleAsync(
            new DemandasAutomationTick(DateTimeOffset.UtcNow, message.Generation),
            DateTimeOffset.UtcNow + DemandasScheduler.Interval
        );
        logger.LogInformation(
            "Demandas {Operation} {Module} {Result} {Reason} {Generation}",
            Operation,
            Module,
            "perpetuated",
            "chain_successor_scheduled",
            message.Generation
        );
    }
}

/// <summary>
/// Semeadura do primeiro tick pós-boot (auto-perpetuante) sob identidade
/// durável: cada boot disputa o claim cluster-wide; só o vencedor semeia a
/// cadeia da geração nova — restarts e réplicas nunca acumulam cadeias
/// paralelas, e a retentativa periódica assume a cadeia se o dono morrer.
/// </summary>
public sealed class DemandasScheduler(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<DemandasScheduler> logger
) : IHostedService, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lease = TimeSpan.FromTicks(3 * Interval.Ticks);

    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private CancellationTokenSource? _retryLoop;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O runtime do Wolverine só ativa depois que TODOS os hosted services
        // subiram (ApplicationStarted): semear antes disso levanta
        // WolverineHasNotStartedException e derruba o boot do host inteiro.
        lifetime.ApplicationStarted.Register(() => _ = ClaimAndSeedAsync());
        _retryLoop = new CancellationTokenSource();
        _ = RetryClaimLoopAsync(_retryLoop.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retentativa periódica de takeover: sem nova infra, apenas o mesmo CAS do
    /// boot — cobre a morte do dono anterior (lease vence e outro host assume).
    /// </summary>
    private async Task RetryClaimLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ClaimAndSeedAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ClaimAndSeedAsync()
    {
        try
        {
            // IMessageBus é scoped no Wolverine: o singleton resolve o barramento
            // dentro de um escopo criado na semeadura.
            using var scope = scopeFactory.CreateScope();
            var claims = scope.ServiceProvider.GetRequiredService<DemandasSchedulerClaimStore>();
            var claim = await claims.TryClaimAsync(_owner, Lease);
            if (!claim.Won)
            {
                return;
            }

            await scope.ServiceProvider
                .GetRequiredService<IMessageBus>()
                .ScheduleAsync(
                    new DemandasAutomationTick(DateTimeOffset.UtcNow, claim.Generation),
                    DateTimeOffset.UtcNow + Interval
                );
            logger.LogInformation(
                "Demandas scheduler chain_seeded {Generation}",
                claim.Generation
            );
        }
        catch (Exception exception)
        {
            // Semeadura é best-effort: o boot nunca falha por causa dela — a
            // retentativa periódica disputa o claim de novo.
            logger.LogError(exception, "Demandas scheduler seed_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose pode preceder StopAsync na ordem de teardown do host: a
        // retentativa periódica é best-effort e nunca derruba o shutdown.
        try
        {
            _retryLoop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _retryLoop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _retryLoop?.Dispose();
    }
}
