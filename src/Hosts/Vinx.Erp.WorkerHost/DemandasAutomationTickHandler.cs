using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Demandas;
using Wolverine;


namespace Vinx.Erp.WorkerHost;

/// <summary>Mensagem de tick da automação de demandas (fila durável local).</summary>
public sealed record DemandasAutomationTick(DateTimeOffset ScheduledAt);

/// <summary>
/// Handler do tick: executa a varredura (avança DUE primeiro, depois overdue)
/// e agenda o próximo tick. Classes públicas com inline-DI — handler
/// interno/lambda é engolido silenciosamente como Handled pela cadeia Wolverine.
/// </summary>
public sealed class DemandasAutomationTickHandler(
    DemandasAutomationScanService scan,
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

        await messageBus.ScheduleAsync(
            new DemandasAutomationTick(DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow + DemandasScheduler.Interval
        );
    }
}

/// <summary>Semeadura do primeiro tick pós-boot (auto-perpetuante).</summary>
public sealed class DemandasScheduler(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<DemandasScheduler> logger
) : IHostedService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // O runtime do Wolverine só ativa depois que TODOS os hosted services
        // subiram (ApplicationStarted): semear antes disso levanta
        // WolverineHasNotStartedException e derruba o boot do host inteiro.
        lifetime.ApplicationStarted.Register(SeedFirstTick);
        return Task.CompletedTask;
    }

    private void SeedFirstTick()
    {
        try
        {
            // IMessageBus é scoped no Wolverine: o singleton resolve o barramento
            // dentro de um escopo criado na semeadura.
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider
                .GetRequiredService<IMessageBus>()
                .ScheduleAsync(
                    new DemandasAutomationTick(DateTimeOffset.UtcNow),
                    DateTimeOffset.UtcNow + Interval
                )
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            // Semeadura é best-effort: o boot nunca falha por causa dela — o
            // próximo deploy semeia de novo e o handler perpetua a cadeia.
            logger.LogError(exception, "Demandas scheduler seed_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
