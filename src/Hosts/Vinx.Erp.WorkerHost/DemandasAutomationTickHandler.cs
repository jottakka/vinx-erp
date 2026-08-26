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
public sealed class DemandasScheduler : IHostedService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IMessageBus _messageBus;

    public DemandasScheduler(IMessageBus messageBus) => _messageBus = messageBus;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _messageBus.ScheduleAsync(
            new DemandasAutomationTick(DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow + Interval
        );
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
