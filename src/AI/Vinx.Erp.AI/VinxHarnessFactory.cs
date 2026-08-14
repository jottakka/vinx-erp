using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Vinx.Erp.AI;

/// <summary>
/// Monta o <see cref="HarnessAgent"/> do Microsoft Agent Framework a partir de
/// um <see cref="IChatClient"/> injetado (agnóstico de provedor), com todos os
/// padrões não usados do harness desligados e aprovação de ferramentas
/// desativada por padrão (fail-closed).
/// </summary>
public static class VinxHarnessFactory
{
    public const int MaximumIterationsPerRequest = 5;

    public static HarnessAgent Create(
        IChatClient chatClient,
        HarnessProbeWriteCounter writeCounter,
        Guid executionRequestId,
        ILoggerFactory? loggerFactory = null)
    {
        var options = new HarnessAgentOptions
        {
            Name = "vinx-erp-harness",
            Description = "Harness supervisionado do VINX ERP; ferramentas limitadas à allowlist VINX.",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    HarnessProbeTools.CreateStatusTool(),
                    HarnessProbeTools.CreateWriteTool(writeCounter, executionRequestId),
                ],

                // O gate processa uma aprovação por resposta; lotes com várias
                // chamadas exigiriam aprovação de todas (inclusive leituras).
                AllowMultipleToolCalls = false,
            },
            DisableWebSearch = true,
            DisableToolAutoApproval = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableOpenTelemetry = true,
            MaximumIterationsPerRequest = MaximumIterationsPerRequest,
        };

        return chatClient.AsHarnessAgent(options, loggerFactory);
    }
}
