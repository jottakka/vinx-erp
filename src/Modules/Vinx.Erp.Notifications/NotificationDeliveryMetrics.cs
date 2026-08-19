using System.Diagnostics.Metrics;

namespace Vinx.Erp.Notifications;

/// <summary>
/// Métricas de entrega com labels de baixa cardinalidade (outcome, canal e
/// motivo estáveis); nunca delivery id, destinatário ou dado do provider.
/// </summary>
public sealed class NotificationDeliveryMetrics
{
    public const string MeterName = "Vinx.Erp.Notifications.Delivery";

    private readonly Histogram<double> _duration;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _deadLetters;

    public NotificationDeliveryMetrics()
    {
        var meter = new Meter(MeterName);
        _duration = meter.CreateHistogram<double>(
            "notification.delivery.duration",
            unit: "ms",
            description: "Duration of a notification delivery attempt including the provider call."
        );
        _outcomes = meter.CreateCounter<long>(
            "notification.delivery.outcomes",
            description: "Notification delivery outcomes by channel."
        );
        _deadLetters = meter.CreateCounter<long>(
            "notification.delivery.dead_letters",
            description: "Notification deliveries moved to the dead-letter queue by stable reason."
        );
    }

    public void RecordOutcome(string channel, string outcome) =>
        _outcomes.Add(1, new KeyValuePair<string, object?>("channel", channel), new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordDuration(string channel, double milliseconds) =>
        _duration.Record(milliseconds, new KeyValuePair<string, object?>("channel", channel));

    public void RecordDeadLetter(string reason) =>
        _deadLetters.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
