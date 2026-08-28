using Microsoft.Extensions.AI;
using System.Text.Json;
using Vinx.Erp.AI;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

/// <summary>
/// Modelo fixo exclusivo da demonstração E2E compartilhada. Guarda somente
/// metadados seguros necessários para provar a ordem autorização-modelo.
/// </summary>
public sealed class DeterministicE2eAgentModel : IAgentModel
{
    public const string ResponseText = "[Demonstração E2E] Resposta determinística do modelo de teste.";

    private int _callCount;
    private int _lastMessageCount;
    private AgentModelSelection? _lastSelection;

    public int CallCount => Volatile.Read(ref _callCount);

    public int LastMessageCount => Volatile.Read(ref _lastMessageCount);

    public AgentModelSelection? LastSelection => Volatile.Read(ref _lastSelection);

    public Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        AgentModelSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _callCount);
        Interlocked.Exchange(ref _lastMessageCount, messages.Count);
        Volatile.Write(ref _lastSelection, selection);

        var text = messages.SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .FirstOrDefault();
        if (text is not null
            && text.Contains(E2eFixtureAttachmentStorage.PaguaiPrompt, StringComparison.Ordinal)
            && text.Contains($"{E2eFixtureSeeder.MariaAlfaAgenticAttachmentId:N}:p1", StringComparison.Ordinal))
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                JsonSerializer.Serialize(new
                {
                    status = "proposal",
                    reason = (string?)null,
                    proposal = new
                    {
                        title = "Nota fiscal sintética",
                        description = "Proposta sintética do anexo E2E.",
                        amount = "250.00",
                        currency = "BRL",
                        dueDate = "2026-09-10",
                        beneficiaryDisplayName = "Fornecedor E2E",
                        proofRequirement = "required",
                        invoiceFileId = E2eFixtureSeeder.MariaAlfaAgenticAttachmentId.ToString("D"),
                        confidence = 0.91,
                        sourceReferences = new[]
                        {
                            new { field = "amount", source = $"{E2eFixtureSeeder.MariaAlfaAgenticAttachmentId:N}:p1" },
                            new { field = "dueDate", source = $"{E2eFixtureSeeder.MariaAlfaAgenticAttachmentId:N}:p1" },
                            new { field = "beneficiaryDisplayName", source = $"{E2eFixtureSeeder.MariaAlfaAgenticAttachmentId:N}:p1" },
                        },
                    },
                }))));
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
    }
}
