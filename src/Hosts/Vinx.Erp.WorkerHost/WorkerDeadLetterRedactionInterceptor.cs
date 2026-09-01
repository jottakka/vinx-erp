using System.Text.Json;
using Vinx.Erp.Demandas;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Permissions;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Notifications;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

internal sealed class WorkerDeadLetterRedactionInterceptor : IDeadLetterInterceptor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    public ValueTask<Exception?> BeforeStoreAsync(
        Envelope envelope,
        Exception? exception,
        CancellationToken cancellation
    )
    {
        RedactEnvelope(envelope);
        if (exception is InboundTelegramDeadLetterException telegramException)
        {
            return ValueTask.FromResult<Exception?>(RedactTelegramException(telegramException));
        }

        if (exception is WorkerPaguaiDraftCreatedDeadLetterException paguaiException)
        {
            return ValueTask.FromResult<Exception?>(RedactPaguaiException(paguaiException));
        }

        if (envelope.Message is PaguaiDraftCreated)
        {
            return ValueTask.FromResult<Exception?>(
                new WorkerPaguaiDraftCreatedDeadLetterException(
                    WorkerPaguaiDraftCreatedDeadLetterException.UnexpectedExceptionType,
                    "Paguai draft notification failed unexpectedly."
                )
            );
        }

        // Fallback neutro: sem tipo de mensagem conhecido, a falha não é
        // reetiquetada como entrega de convite (nem de membro) — o motivo
        // real permanece visível na DLQ.
        return ValueTask.FromResult<Exception?>(
            new WorkerUnknownEnvelopeDeadLetterException(
                WorkerUnknownEnvelopeDeadLetterException.UnexpectedExceptionType,
                "Worker dead-letter handling failed unexpectedly."
            )
        );
    }

    private static InboundTelegramDeadLetterException RedactTelegramException(
        InboundTelegramDeadLetterException exception
    ) =>
        exception.ExceptionType switch
        {
            InboundTelegramDeadLetterException.InvalidExceptionType => new(
                InboundTelegramDeadLetterException.InvalidExceptionType,
                "Inbound Telegram receipt rejected the envelope."
            ),
            InboundTelegramDeadLetterException.RetryExhaustedExceptionType => new(
                InboundTelegramDeadLetterException.RetryExhaustedExceptionType,
                "Inbound Telegram receipt exhausted transient retries."
            ),
            InboundTelegramDeadLetterException.UnexpectedExceptionType => new(
                InboundTelegramDeadLetterException.UnexpectedExceptionType,
                "Inbound Telegram receipt failed unexpectedly."
            ),
            _ => new(
                InboundTelegramDeadLetterException.UnexpectedExceptionType,
                "Inbound Telegram receipt failed unexpectedly."
            ),
        };

    private static WorkerPaguaiDraftCreatedDeadLetterException RedactPaguaiException(
        WorkerPaguaiDraftCreatedDeadLetterException exception
    ) =>
        exception.ExceptionType switch
        {
            WorkerPaguaiDraftCreatedDeadLetterException.InvalidExceptionType => new(
                WorkerPaguaiDraftCreatedDeadLetterException.InvalidExceptionType,
                "Paguai draft notification rejected the envelope."
            ),
            WorkerPaguaiDraftCreatedDeadLetterException.RetryExhaustedExceptionType => new(
                WorkerPaguaiDraftCreatedDeadLetterException.RetryExhaustedExceptionType,
                "Paguai draft notification exhausted transient retries."
            ),
            WorkerPaguaiDraftCreatedDeadLetterException.UnexpectedExceptionType => new(
                WorkerPaguaiDraftCreatedDeadLetterException.UnexpectedExceptionType,
                "Paguai draft notification failed unexpectedly."
            ),
            _ => new(
                WorkerPaguaiDraftCreatedDeadLetterException.UnexpectedExceptionType,
                "Paguai draft notification failed unexpectedly."
            ),
        };

    /// <summary>
    /// Redige por tipo de mensagem: o corpo vira o registro vazio do próprio
    /// tipo e os headers diagnósticos são descartados antes da DLQ. Tipo não
    /// mapeado NÃO é reescrito para outro tipo: ganha marcador neutro e fica
    /// visível para diagnóstico.
    /// </summary>
    internal static void RedactEnvelope(Envelope envelope)
    {
        switch (envelope.Message)
        {
            case NotificationDeliveryRequested:
                var redactedNotification = new NotificationDeliveryRequested(Guid.Empty);
                envelope.Message = redactedNotification;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedNotification,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case MemberInvitationDeliveryRequested:
                var redactedMemberInvitation = new MemberInvitationDeliveryRequested(Guid.Empty);
                envelope.Message = redactedMemberInvitation;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedMemberInvitation,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case OwnerInvitationDeliveryRequested:
                var redactedOwnerInvitation = new OwnerInvitationDeliveryRequested(Guid.Empty);
                envelope.Message = redactedOwnerInvitation;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedOwnerInvitation,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundAgentRequestReceived:
                var redactedAgentRequest = new InboundAgentRequestReceived(
                    Guid.Empty,
                    AgentChannelKind.Web
                );
                envelope.Message = redactedAgentRequest;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedAgentRequest,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case DemandasAutomationTick:
                var redactedAutomationTick = new DemandasAutomationTick(default, 0);
                envelope.Message = redactedAutomationTick;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedAutomationTick,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundWhatsAppImageReceived:
                var redactedInbound = new InboundWhatsAppImageReceived(Guid.Empty);
                envelope.Message = redactedInbound;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedInbound,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundWhatsAppTextCorrelationExpired:
                var redactedExpiry = new InboundWhatsAppTextCorrelationExpired(Guid.Empty);
                envelope.Message = redactedExpiry;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedExpiry,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundWhatsAppDocumentReceived:
                var redactedDocument = new InboundWhatsAppDocumentReceived(Guid.Empty);
                envelope.Message = redactedDocument;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedDocument,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case InboundTelegramTextReceived:
                var redactedTelegram = new InboundTelegramTextReceived(Guid.Empty);
                envelope.Message = redactedTelegram;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    redactedTelegram,
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
            case PaguaiDraftCreated:
                var redactedPaguai = new PaguaiDraftCreated(Guid.Empty, Guid.Empty, Guid.Empty);
                envelope.Message = redactedPaguai;
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(redactedPaguai, SerializerOptions);
                envelope.Headers.Clear();
                break;
            default:
                // Tipo desconhecido: sem PII conhecida para remover e sem
                // registro vazio para construir, o corpo vira um marcador
                // neutro — o envelope permanece identificável na DLQ em vez
                // de ser reetiquetado como outro tipo de mensagem.
                envelope.Data = JsonSerializer.SerializeToUtf8Bytes(
                    new RedactedDeadLetterBody(envelope.Message?.GetType().FullName),
                    SerializerOptions
                );
                envelope.Headers.Clear();
                break;
        }
    }

    private sealed record RedactedDeadLetterBody(string? MessageType);
}

/// <summary>
/// Classificação neutra de DLQ para tipos de mensagem sem handler dedicado:
/// evita reetiquetar falhas futuras como entrega de convite.
/// </summary>
internal sealed class WorkerUnknownEnvelopeDeadLetterException(
    string exceptionType,
    string exceptionMessage
    )
        : Exception,
        IDeadLetterExceptionInfo
{
    public const string UnexpectedExceptionType = "worker_dead_letter_unexpected";

    public string ExceptionType { get; } = exceptionType;

    public string ExceptionMessage { get; } = exceptionMessage;
}
