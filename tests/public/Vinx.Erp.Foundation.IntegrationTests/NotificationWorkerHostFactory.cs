extern alias workerhost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Notifications.Providers.Telegram;
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
/// Bloqueia localmente qualquer envio inesperado pelo client Telegram padrão
/// das factories de teste, sem deixar o handler alcançar a rede.
/// </summary>
internal sealed class FailFastTelegramBotHandler : DelegatingHandler
{
    private int _calls;

    public FailFastTelegramBotHandler()
        : base(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException("Telegram Bot API network access is disabled in tests.");
    }
}

/// <summary>
/// WorkerHost de teste com providers externos substituíveis e defaults
/// determinísticos que impedem configuração ambiente ou tráfego acidental.
/// </summary>
internal sealed class NotificationWorkerHostFactory(
    string connectionString,
    bool useFakeWhatsAppProvider = false,
    string? debugLogPath = null,
    HttpMessageHandler? metaGraphHandler = null,
    Vinx.Erp.Foundation.Files.FileStorage? testFileStorage = null,
    Vinx.Erp.Foundation.Files.IMalwareScanner? testMalwareScanner = null,
    Vinx.Erp.Notifications.Providers.Email.EmailProviderOptions? smtpOptions = null,
    HttpMessageHandler? telegramBotHandler = null,
    IReadOnlyDictionary<string, string>? telegramSettings = null,
    bool useCanonicalTelegramHttpRegistration = false
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
        // Sempre sombreia o ambiente do processo antes de qualquer fake ou
        // override explícito: uma factory padrão não pode herdar credenciais.
        builder.UseSetting("TELEGRAM_BOT_TOKEN", "");
        builder.UseSetting("TELEGRAM_API_BASE_URL", "https://api.telegram.test");
        builder.UseSetting("TELEGRAM_WEBHOOK_SECRET_TOKEN", "");
        // Segredos de teste sem valor real; o client do Graph é substituído
        // por handler falso nos testes de envio.
        builder.UseSetting("META_WHATSAPP_PHONE_NUMBER_ID", "111111111111111");
        builder.UseSetting("META_WHATSAPP_ACCESS_TOKEN", "test-meta-access-token");
        builder.UseSetting("META_WHATSAPP_WEB_APP_BASE_URL", "https://app.example.test");
        builder.UseSetting("META_WHATSAPP_APP_SECRET", "test-meta-app-secret");
        builder.UseSetting("META_WHATSAPP_WEBHOOK_VERIFY_TOKEN", "test-meta-verify-token");
        if (testFileStorage is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Vinx.Erp.Foundation.Files.FileStorage>();
                services.AddSingleton(testFileStorage);
            });
        }
        else
        {
            // Sem fake: força o fail-closed determinístico, imune a variáveis
            // de ambiente do processo de teste (Supabase local, etc.).
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Vinx.Erp.Foundation.Files.FileStorage>();
                services.AddSingleton<Vinx.Erp.Foundation.Files.FileStorage>(
                    new Vinx.Erp.Foundation.Files.BlockedFileStorage()
                );
            });
        }

        if (testMalwareScanner is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Vinx.Erp.Foundation.Files.IMalwareScanner>();
                services.AddSingleton(testMalwareScanner);
            });
        }
        else
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Vinx.Erp.Foundation.Files.IMalwareScanner>();
                services.AddSingleton<Vinx.Erp.Foundation.Files.IMalwareScanner>(
                    new Vinx.Erp.Foundation.Files.BlockedMalwareScanner()
                );
            });
        }

        if (metaGraphHandler is not null)
        {
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(MetaWhatsAppClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => metaGraphHandler)
            );
        }

        if (smtpOptions is not null)
        {
            // Substitui o singleton de opções SMTP para apontar o provider de
            // e-mail ao servidor SMTP falso em loopback do teste (sem TLS).
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<
                    Vinx.Erp.Notifications.Providers.Email.EmailProviderOptions
                >();
                services.AddSingleton(smtpOptions);
            });
        }

        if (telegramBotHandler is not null)
        {
            // Perfil de envio somente para o Bot API falso. Os placeholders
            // não representam uma credencial e o handler intercepta antes de
            // qualquer tráfego externo.
            builder.UseSetting("TELEGRAM_BOT_TOKEN", "123456:telegram-test-placeholder");
            builder.UseSetting("TELEGRAM_API_BASE_URL", "https://api.telegram.test");
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(TelegramClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => telegramBotHandler)
            );
        }
        else if (!useCanonicalTelegramHttpRegistration)
        {
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(TelegramClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FailFastTelegramBotHandler())
            );
        }

        if (telegramSettings is not null)
        {
            foreach (var (key, value) in telegramSettings)
            {
                builder.UseSetting(key, value);
            }
        }

        if (
            telegramBotHandler is null
            && (telegramSettings is null || !telegramSettings.ContainsKey("TELEGRAM_API_BASE_URL"))
        )
        {
            builder.ConfigureTestServices(services =>
                services.PostConfigure<TelegramOptions>(options =>
                {
                    // O host trata token ausente com sua base pública como
                    // configuração ausente. A URL .test continua no
                    // configuration root para sombrear o ambiente, mas não
                    // transforma esse default da factory em configuração
                    // parcial para a validação do WorkerHost.
                    if (
                        string.IsNullOrWhiteSpace(options.BotToken)
                        && string.Equals(
                            options.ApiBaseUrl,
                            "https://api.telegram.test",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        options.ApiBaseUrl = "https://api.telegram.org";
                    }
                })
            );
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
