extern alias workerhost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using WorkerProgram = workerhost::Program;

namespace Vinx.Erp.Foundation.IntegrationTests;

internal sealed class FileLogger(string path) : ILogger
{
    private readonly Lock _gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        lock (_gate)
        {
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O} {logLevel} {formatter(state, exception)} {exception?.Message}\n"
            );
        }
    }
}

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(path);

    public void Dispose() { }
}

/// <summary>
/// WorkerHost de teste com provider de WhatsApp substituível; a ausência do
/// substituto reproduz a configuração de produção desta fatia (somente
/// in-app registrado).
/// </summary>
internal sealed class NotificationWorkerHostFactory(
    string connectionString,
    bool useFakeWhatsAppProvider = false,
    string? debugLogPath = null,
    HttpMessageHandler? metaGraphHandler = null,
    bool configureInboundAi = false,
    HttpMessageHandler? inboundTextAiHandler = null,
    HttpMessageHandler? inboundVisionAiHandler = null
)
    : WebApplicationFactory<WorkerProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FOUNDATION_WORKER_CONNECTION", connectionString);
        builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.test/auth/v1");
        builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
        builder.UseSetting("SUPABASE_AUTH_SERVICE_ROLE_KEY", "test-service-role-key");
        builder.UseSetting("SUPABASE_INVITE_REDIRECT_URL", "https://app.example.test/convite");
        // Segredos de teste sem valor real; o client do Graph é substituído
        // por handler falso nos testes de envio.
        builder.UseSetting("META_WHATSAPP_PHONE_NUMBER_ID", "111111111111111");
        builder.UseSetting("META_WHATSAPP_ACCESS_TOKEN", "test-meta-access-token");
        builder.UseSetting("META_WHATSAPP_WEB_APP_BASE_URL", "https://app.example.test");
        builder.UseSetting("META_WHATSAPP_APP_SECRET", "test-meta-app-secret");
        builder.UseSetting("META_WHATSAPP_WEBHOOK_VERIFY_TOKEN", "test-meta-verify-token");
        if (metaGraphHandler is not null)
        {
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(MetaWhatsAppClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => metaGraphHandler)
            );
        }

        if (configureInboundAi)
        {
            // Perfis completos e sem valor real: endpoints https públicos são
            // exigidos pela política de egresso do #135-B; os handlers falsos
            // interceptam antes de qualquer rede real. A instância substitui o
            // singleton diretamente — o bind de seção não é usado em teste.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<InboundWhatsAppAiOptions>();
                services.AddSingleton(InboundAiTestProfiles.Create());
            });
            if (inboundTextAiHandler is not null)
            {
                builder.ConfigureTestServices(services =>
                    services
                        .AddHttpClient(InboundWhatsAppInteractionService.TextHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => inboundTextAiHandler)
                );
            }

            if (inboundVisionAiHandler is not null)
            {
                builder.ConfigureTestServices(services =>
                    services
                        .AddHttpClient(InboundWhatsAppInteractionService.VisionHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => inboundVisionAiHandler)
                );
            }
        }

        if (debugLogPath is not null)
        {
            builder.ConfigureLogging(logging =>
                logging.AddProvider(new FileLoggerProvider(debugLogPath))
            );
        }

        if (!useFakeWhatsAppProvider)
        {
            return;
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<INotificationProvider>();
            services.AddScoped<INotificationProvider, FakeNotificationProvider>();
            services.AddScoped<INotificationProvider, InAppNotificationProvider>();
        });
    }
}

/// <summary>
/// Provider falso de WhatsApp registrado como tipo público concreto: o
/// codegen do Wolverine inline-a dependências públicas tipadas, enquanto
/// tipos internos ou factories opacas (lambdas) fazem a geração do handler
/// falhar com ServiceLocationPolicy NotAllowed. O estado é estático e
/// resetado por teste; as classes de teste compartilham coleção xunit e
/// executam serializadas.
/// </summary>
public sealed class FakeNotificationProvider : INotificationProvider
{
    private static int _calls;
    private static NotificationProviderResultKind _resultKind =
        NotificationProviderResultKind.Delivered;
    private static bool _throwTransient;

    public string Channel => "whatsapp";

    public static int Calls => Volatile.Read(ref _calls);

    public static NotificationProviderResultKind ResultKind
    {
        get => _resultKind;
        set => _resultKind = value;
    }

    public static bool ThrowTransient
    {
        get => _throwTransient;
        set => _throwTransient = value;
    }

    public static void Reset(
        NotificationProviderResultKind resultKind = NotificationProviderResultKind.Delivered,
        bool throwTransient = false
    )
    {
        Interlocked.Exchange(ref _calls, 0);
        _resultKind = resultKind;
        _throwTransient = throwTransient;
    }

    public Task<NotificationProviderResult> DeliverAsync(
        NotificationDeliveryRequest request,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _calls);
        if (_throwTransient)
        {
            throw new NotificationProviderTransientException("fake provider transient failure");
        }

        return Task.FromResult(new NotificationProviderResult(_resultKind));
    }
}
