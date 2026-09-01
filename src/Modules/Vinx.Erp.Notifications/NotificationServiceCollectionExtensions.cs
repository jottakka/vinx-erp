using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Notifications.Providers.Email;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Notifications.Providers.Telegram;

namespace Vinx.Erp.Notifications;

/// <summary>
/// Raízes de composição do módulo Notifications. Cada host registra somente a
/// fatia que consome: o ApiHost admite o ingress do WhatsApp; o WorkerHost
/// entrega notificações e processa o inbound. Nenhuma outra fatia é
/// registrada para não alterar o conjunto de registros de cada host.
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Admissão inbound do WhatsApp: resolvedor de ator via função SECURITY
    /// DEFINER e o serviço de ingress deduplicado — ApiHost.
    /// </summary>
    public static IServiceCollection AddWhatsAppIngress(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IInboundActorResolver, SqlInboundActorResolver>();
        services.AddScoped<InboundWhatsAppIngressService>();
        return services;
    }

    /// <summary>
    /// Entrega de notificações por canal: métricas, resolvedor de provider, os
    /// quatro providers (in-app, Meta WhatsApp, e-mail e Telegram) e o serviço
    /// do worker com claim e retry — WorkerHost.
    /// </summary>
    public static IServiceCollection AddNotificationDelivery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<NotificationDeliveryMetrics>();
        services.AddScoped<NotificationProviderResolver>();
        services.AddScoped<INotificationProvider, InAppNotificationProvider>();
        services.AddScoped<INotificationProvider, MetaWhatsAppProvider>();
        services.AddScoped<INotificationProvider, EmailNotificationProvider>();
        services.AddScoped<INotificationProvider, TelegramProvider>();
        services.AddScoped<NotificationDeliveryWorkerService>();
        return services;
    }

    /// <summary>
    /// Processamento do inbound do WhatsApp no worker: interação, documento,
    /// materialização de anexo e o adapter do canal do agente — WorkerHost.
    /// </summary>
    public static IServiceCollection AddInboundWhatsAppProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<InboundWhatsAppInteractionService>();
        services.AddScoped<InboundWhatsAppDocumentService>();
        services.AddScoped<InboundAttachmentMaterializer>();
        services.AddScoped<InboundAgentChannelAdapter>();
        return services;
    }
}
