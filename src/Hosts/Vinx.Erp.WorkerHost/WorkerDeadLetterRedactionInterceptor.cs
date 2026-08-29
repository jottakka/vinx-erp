using System.Text.Json;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Permissions;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Notifications;
using Vinx.Erp.Paguai;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace Vinx.Erp.WorkerHost;

/// <summary>Marcador de redação para envelopes sem gêmeo vazio conhecido: preserva
/// a existência da mensagem na DLQ sem inventar um tipo operacional alheio.</summary>
public sealed record RedactedUnknownWorkerMessage;

/// <summary>Classificação opaca para envelopes sem tipo conhecido: a DLQ registra
/// a falha sem reclassificá-la como entrega de convite de owner.</summary>
internal sealed class WorkerRedactedUnknownDeadLetterException(string exceptionMessage)
    : Exception(exceptionMessage),
        IDeadLetterExceptionInfo
{
    public const string UnexpectedExceptionType = "worker_unknown_unexpected";

    public string ExceptionType { get; } = UnexpectedExceptionType;

    public string ExceptionMessage { get; } = exceptionMessage;
}

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

        if (envelope.Message is OwnerInvitationDeliveryRequested)
        {
            return ValueTask.FromResult<Exception?>(
                new WorkerOwnerInvitationDeadLetterException(
                    WorkerOwnerInvitationDeadLetterException.UnexpectedExceptionType,
                    "Worker delivery ingress failed unexpectedly."
                )
            );
        }

        if (envelope.Message is MemberInvitationDeliveryRequested)
        {
            return ValueTask.FromResult<Exception?>(
                new WorkerRedactedUnknownDeadLetterException(
                    "Worker member invitation delivery failed unexpectedly."
                )
            );
        }

        return ValueTask.FromResult<Exception?>(
            new WorkerRedactedUnknownDeadLetterException(
                "Worker delivery ingress failed unexpectedly."
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
    /// tipo (o canal/geração permanece quando é operacional e não sensível) e
    /// os headers diagnósticos são descartados antes da DLQ. Envelope sem
    /// gêmeo conhecido vira marcador opaco — nunca um tipo alheio.
    /// </summary>
    internal static void RedactEnvelope(Envelope envelope)
    {
        switch (envelope.Message)
        {
            case NotificationDeliveryRequested:
                RedactAs(envelope, new NotificationDeliveryRequested(Guid.Empty));
                break;
            case InboundWhatsAppImageReceived:
                RedactAs(envelope, new InboundWhatsAppImageReceived(Guid.Empty));
                break;
            case InboundWhatsAppTextCorrelationExpired:
                RedactAs(envelope, new InboundWhatsAppTextCorrelationExpired(Guid.Empty));
                break;
            case InboundWhatsAppDocumentReceived:
                RedactAs(envelope, new InboundWhatsAppDocumentReceived(Guid.Empty));
                break;
            case InboundTelegramTextReceived:
                RedactAs(envelope, new InboundTelegramTextReceived(Guid.Empty));
                break;
            case InboundAgentRequestReceived agent:
                // Preserva o canal (enum operacional) e zera o identificador sensível.
                RedactAs(envelope, new InboundAgentRequestReceived(Guid.Empty, agent.Channel));
                break;
            case PaguaiDraftCreated:
                RedactAs(envelope, new PaguaiDraftCreated(Guid.Empty, Guid.Empty, Guid.Empty));
                break;
            case OwnerInvitationDeliveryRequested:
                RedactAs(envelope, new OwnerInvitationDeliveryRequested(Guid.Empty));
                break;
            case MemberInvitationDeliveryRequested:
                RedactAs(envelope, new MemberInvitationDeliveryRequested(Guid.Empty));
                break;
            case DemandasAutomationTick tick:
                // Geração e agendamento são operacionais e não carregam dado de negócio.
                RedactAs(envelope, new DemandasAutomationTick(tick.ScheduledAt, tick.Generation));
                break;
            default:
                RedactAs(envelope, new RedactedUnknownWorkerMessage());
                break;
        }
    }

    private static void RedactAs<T>(Envelope envelope, T redacted)
    {
        envelope.Message = redacted;
        envelope.Data = JsonSerializer.SerializeToUtf8Bytes(redacted, SerializerOptions);
        envelope.Headers.Clear();
    }
}
