namespace Vinx.Erp.AI;

internal interface IAgentExecutionLifecycle
{
    Task MarkVisionInFlightAsync(CancellationToken cancellationToken);
    Task MarkModelInFlightAsync(CancellationToken cancellationToken);
}
