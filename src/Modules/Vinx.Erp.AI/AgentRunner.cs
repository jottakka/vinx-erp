using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.AI;

/// <summary>
/// Orquestra uma execução do agente na ordem fixa da spec: validação de
/// entrada, resolução de contexto confiável, seleção de provider e UMA chamada
/// de modelo no máximo, toda falha é fail-closed. Stateless: nenhum estado
/// sobrevive entre chamadas. Tool call volta como proposta que exige
/// confirmação — o runner não executa nada. Com gate de saída configurado, a
/// resposta do modelo só chega ao canal via decisão do gate (o gate não
/// redige); sem gate, o comportamento é o histórico. Nunca loga prompt,
/// resposta ou PII.
/// </summary>
public sealed class AgentRunner(
    IAgentContextResolver contextResolver,
    IAgentModel? model,
    ILogger<AgentRunner> logger,
    Func<string, AgentTaskProfile?>? profileCatalog = null,
    AgentOutputGate? outputGate = null,
    DocumentContextNormalizer? documentContextNormalizer = null)
{
    private const string Operation = "run";
    private const string Module = "Vinx.Erp.AI";
    private const string ExactlyOneAttachmentRequired = "exactly_one_attachment_required";
    private const string ExecutionLogTemplate = "Agent execution {Operation} {Module} {Result} {Reason} {ElapsedMs}";

    private static readonly IReadOnlySet<string> NoRequiredOutputFields = new HashSet<string>();

    private readonly Func<string, AgentTaskProfile?> lookupProfile = profileCatalog ?? LookupInCatalog;

    public Task<AgentOutcome> RunAsync(
        ActorContext actor,
        string profileId,
        string message,
        Guid? requestedCompanyId,
        CancellationToken cancellationToken) =>
        RunAsync(actor, profileId, message, requestedCompanyId, Array.Empty<Guid>(), cancellationToken);

    public async Task<AgentOutcome> RunAsync(
        ActorContext actor,
        string profileId,
        string message,
        Guid? requestedCompanyId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
        => await RunAsync(actor, profileId, message, requestedCompanyId, attachmentIds, lifecycle: null, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<AgentOutcome> RunAsync(
        ActorContext actor,
        string profileId,
        string message,
        Guid? requestedCompanyId,
        IReadOnlyList<Guid> attachmentIds,
        IAgentExecutionLifecycle? lifecycle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var stopwatch = Stopwatch.StartNew();

        // 1. Validação antes de qualquer consulta ou modelo.
        var profile = lookupProfile(profileId);
        var invalidReason = AgentPolicy.ValidateRequest(message, profile);
        if (invalidReason is not null)
        {
            return Finish(AgentOutcomeStatus.Invalid, invalidReason, actor, stopwatch);
        }

        if (attachmentIds.Count > 0 && !profile!.AllowDocumentAttachments)
        {
            return Finish(AgentOutcomeStatus.Invalid, AgentOutcomeReason.AttachmentsNotAllowed, actor, stopwatch);
        }

        if (profile!.ProfileId == AgentTaskProfileCatalog.PaguaiFromDocumentProfileId && attachmentIds.Count != 1)
        {
            return Finish(AgentOutcomeStatus.Invalid, ExactlyOneAttachmentRequired, actor, stopwatch);
        }

        // 2. Contexto confiável resolvido ANTES do modelo; reason copiado.
        var resolution = await contextResolver.ResolveAsync(actor, profile!, requestedCompanyId, cancellationToken);
        if (resolution.Status != AgentContextResolutionStatus.Resolved)
        {
            return Finish(MapResolutionStatus(resolution.Status), resolution.ReasonCode, actor, stopwatch);
        }

        if (attachmentIds.Count > 0 && documentContextNormalizer is null)
        {
            return Finish(
                AgentOutcomeStatus.NeedsClarification,
                AgentOutcomeReason.DocumentContextUnavailable,
                actor,
                stopwatch);
        }

        var turnText = message;
        PaguaiDocumentProposalParseRequest? paguaiDocumentProposalRequest = null;
        if (attachmentIds.Count > 0)
        {
            var documentContexts = new List<DocumentContextResult>(attachmentIds.Count);
            foreach (var attachmentId in attachmentIds)
            {
                var documentContext = await documentContextNormalizer!
                    .BuildContextAsync(
                        actor,
                        resolution.Context!.CompanyId,
                        attachmentId,
                        lifecycle,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (documentContext.Status is not DocumentContextStatus.Ready)
                {
                    return Finish(
                        AgentOutcomeStatus.NeedsClarification,
                        AgentOutcomeReason.DocumentContextUnavailable,
                        actor,
                        stopwatch);
                }

                documentContexts.Add(documentContext);
            }

            turnText = ComposeTurn(message, documentContexts, profile!.MaxInputCharacters);
            if (profile.ProfileId == AgentTaskProfileCatalog.PaguaiFromDocumentProfileId)
            {
                paguaiDocumentProposalRequest = new PaguaiDocumentProposalParseRequest(
                    documentContexts[0].FileRecordId,
                    documentContexts.SelectMany(document => document.Segments)
                        .Select(segment => segment.SourceRef)
                        .ToHashSet(StringComparer.Ordinal),
                    StructuredJson: string.Empty);
            }
        }

        // 3. Provider não atribuído ou cliente ausente: fail-closed até #135.
        if (model is null || profile!.ModelSelection is null)
        {
            return Finish(AgentOutcomeStatus.Unavailable, AgentOutcomeReason.ProviderNotConfigured, actor, stopwatch);
        }

        // 4. Turn normalizado: uma única mensagem do usuário, uma chamada.
        var messages = new List<ChatMessage> { new(ChatRole.User, turnText) };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(profile.Timeout);

        ChatResponse response;
        try
        {
            // A transição durável acontece imediatamente antes da única chamada
            // principal; qualquer saída incerta depois dela é ambígua.
            if (lifecycle is not null)
            {
                await lifecycle.MarkModelInFlightAsync(cancellationToken).ConfigureAwait(false);
            }

            response = await model.GetResponseAsync(messages, profile.ModelSelection, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Finish(
                AgentOutcomeStatus.TimedOut,
                AgentOutcomeReason.ModelTimeout,
                actor,
                stopwatch,
                exception: exception);
        }
        catch (Exception exception)
        {
            return Finish(
                AgentOutcomeStatus.Unavailable,
                AgentOutcomeReason.ModelUnavailable,
                actor,
                stopwatch,
                exception: exception);
        }

        // 5. Normalização do outcome: tool call é proposta, nunca execução.
        var toolCall = response.Messages
            .SelectMany(messageContent => messageContent.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();

        // 6. Gate de saída: só roda quando houve resposta; o texto liberado é
        // o do modelo (o gate não redige) e o contrato vem do perfil aprovado.
        if (outputGate is not null)
        {
            var gated = outputGate.Evaluate(new AgentOutputGateRequest(
                resolution.Context!,
                ToModelResponse(
                    response,
                    toolCall is not null,
                    profile!.ProfileId == AgentTaskProfileCatalog.PaguaiFromDocumentProfileId),
                new AgentOutputContract(profile!.Journey, profile.RequiredOutputFields ?? NoRequiredOutputFields),
                profile.SourcesRequired,
                paguaiDocumentProposalRequest));

            // Proposta de tool domina o desfecho do gate: segue exigindo
            // confirmação, com o texto que o gate liberou.
            if (toolCall is not null)
            {
                return Finish(
                    AgentOutcomeStatus.RequiresConfirmation,
                    AgentOutcomeReason.ToolProposalRequiresConfirmation,
                    actor,
                    stopwatch,
                    text: gated.Text,
                    proposedActionName: toolCall.Name,
                    policyVersion: gated.PolicyVersion);
            }

            var gatedStatus = MapOutputGateStatus(gated.Status);
            return Finish(
                gatedStatus,
                gated.ReasonCode,
                actor,
                stopwatch,
                text: gated.Text,
                paguaiProposal: gated.PaguaiProposal,
                policyVersion: gated.PolicyVersion);
        }

        if (toolCall is not null)
        {
            return Finish(
                AgentOutcomeStatus.RequiresConfirmation,
                AgentOutcomeReason.ToolProposalRequiresConfirmation,
                actor,
                stopwatch,
                proposedActionName: toolCall.Name);
        }

        return Finish(AgentOutcomeStatus.Completed, AgentOutcomeReason.Completed, actor, stopwatch, text: response.Text);
    }

    /// <summary>
    /// Mapeamento único ChatResponse → AgentModelResponse (v1): primeiro texto,
    /// proposta de tool pela presença de FunctionCallContent, sem JSON
    /// estruturado, sem fontes citadas e sem confiança — as regras completas
    /// permanecem no gate.
    /// </summary>
    private static AgentModelResponse ToModelResponse(
        ChatResponse response,
        bool hasToolProposal,
        bool structuredOutputRequired)
    {
        var firstText = response.Messages
            .SelectMany(messageContent => messageContent.Contents)
            .OfType<TextContent>()
            .FirstOrDefault();

        return new AgentModelResponse(
            structuredOutputRequired ? null : firstText?.Text,
            StructuredJson: structuredOutputRequired ? firstText?.Text : null,
            CitedSources: [],
            Confidence: null,
            HasToolProposal: hasToolProposal);
    }

    private static string ComposeTurn(
        string message,
        IReadOnlyList<DocumentContextResult> documentContexts,
        int maxInputCharacters)
    {
        var totalTextCharacters = checked(documentContexts.Sum(document =>
            document.Segments.Sum(segment => segment.Text.Length)));
        var fullTurn = BuildTurn(message, documentContexts, totalTextCharacters);
        if (fullTurn.Length <= maxInputCharacters)
        {
            return fullTurn;
        }

        var lower = 0;
        var upper = totalTextCharacters;
        string? admitted = null;
        while (lower <= upper)
        {
            var candidateTextCharacters = lower + ((upper - lower) / 2);
            var candidate = BuildTurn(message, documentContexts, candidateTextCharacters);
            if (candidate.Length <= maxInputCharacters)
            {
                admitted = candidate;
                lower = candidateTextCharacters + 1;
            }
            else
            {
                upper = candidateTextCharacters - 1;
            }
        }

        return admitted ?? throw new InvalidOperationException(
            "The approved main input limit cannot contain the document context framing.");
    }

    private static string BuildTurn(
        string message,
        IReadOnlyList<DocumentContextResult> documentContexts,
        int textCharacterBudget)
    {
        var remaining = textCharacterBudget;
        var documents = documentContexts.Select(document =>
        {
            var aggregateTruncated = false;
            var segments = document.Segments.Select(segment =>
            {
                string text;
                if (remaining >= segment.Text.Length)
                {
                    text = segment.Text;
                    remaining -= segment.Text.Length;
                }
                else
                {
                    var safeLength = SafePrefixLength(segment.Text, remaining);
                    text = segment.Text[..safeLength];
                    remaining = 0;
                    aggregateTruncated = true;
                }

                return new
                {
                    source = segment.SourceRef,
                    untrusted = segment.Untrusted,
                    text,
                };
            }).ToArray();
            return new
            {
                truncated = document.Truncated || aggregateTruncated,
                totalCharacters = document.TotalCharacters,
                segments,
            };
        }).ToArray();
        var context = JsonSerializer.Serialize(new
        {
            untrusted = true,
            documents,
        });
        return $"{message}\n\n--- BEGIN UNTRUSTED DOCUMENT CONTEXT ---\n{context}\n--- END UNTRUSTED DOCUMENT CONTEXT ---";
    }

    private static int SafePrefixLength(string text, int requestedLength)
    {
        if (requestedLength <= 0)
        {
            return 0;
        }

        if (requestedLength >= text.Length)
        {
            return text.Length;
        }

        return char.IsHighSurrogate(text[requestedLength - 1])
            && char.IsLowSurrogate(text[requestedLength])
                ? requestedLength - 1
                : requestedLength;
    }

    private AgentOutcome Finish(
        AgentOutcomeStatus status,
        string reasonCode,
        ActorContext actor,
        Stopwatch stopwatch,
        string? text = null,
        string? proposedActionName = null,
        PaguaiDocumentProposal? paguaiProposal = null,
        string? policyVersion = null,
        Exception? exception = null)
    {
        stopwatch.Stop();
        var outcome = new AgentOutcome(
            status,
            reasonCode,
            text,
            proposedActionName,
            actor.CorrelationId,
            stopwatch.Elapsed,
            paguaiProposal,
            policyVersion);
        AgentTelemetry.RecordExecution(status, reasonCode);

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = actor.CorrelationId });
        LogOutcome(outcome, exception);
        return outcome;
    }

    private void LogOutcome(AgentOutcome outcome, Exception? exception)
    {
        var result = AgentTelemetry.ToResultTag(outcome.Status);
        var elapsedMs = (long)outcome.Elapsed.TotalMilliseconds;

        // Níveis: validação esperada usa Information (regra AGENTS); Warning
        // apenas para negação; Error preservando a exceção do modelo.
        switch (outcome.Status)
        {
            case AgentOutcomeStatus.Denied:
                logger.LogWarning(ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
                break;
            case AgentOutcomeStatus.Unavailable:
            case AgentOutcomeStatus.TimedOut:
                logger.LogError(exception, ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
                break;
            default:
#pragma warning disable CA1873 // Os argumentos são constantes ou valores baratos; o guard de logging não agrega ganho.
                logger.LogInformation(ExecutionLogTemplate, Operation, Module, result, outcome.ReasonCode, elapsedMs);
#pragma warning restore CA1873
                break;
        }
    }

    private static AgentOutcomeStatus MapOutputGateStatus(AgentOutputGateStatus status) => status switch
    {
        AgentOutputGateStatus.Result => AgentOutcomeStatus.Completed,
        AgentOutputGateStatus.NeedsClarification => AgentOutcomeStatus.NeedsClarification,
        AgentOutputGateStatus.Denied => AgentOutcomeStatus.Denied,
        AgentOutputGateStatus.ValidationError => AgentOutcomeStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported output gate status."),
    };

    private static AgentOutcomeStatus MapResolutionStatus(AgentContextResolutionStatus status) => status switch
    {
        AgentContextResolutionStatus.NeedsClarification => AgentOutcomeStatus.NeedsClarification,
        AgentContextResolutionStatus.Denied => AgentOutcomeStatus.Denied,
        AgentContextResolutionStatus.Unavailable => AgentOutcomeStatus.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported context resolution status."),
    };

    private static AgentTaskProfile? LookupInCatalog(string profileId) =>
        AgentTaskProfileCatalog.TryGet(profileId, out var profile) ? profile : null;
}
