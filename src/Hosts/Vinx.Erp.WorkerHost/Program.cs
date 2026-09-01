using JasperFx;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Configuration;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Permissions;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Demandas;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Email;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Notifications.Providers.Telegram;
using Vinx.Erp.Paguai;
using Vinx.Erp.WorkerHost;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);
builder.AddVinxHostDefaults();
// Medidor do módulo Notifications: a Foundation não referencia módulos,
// portanto a fonte é registrada aqui, no host que entrega as notificações.
builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
    metrics.AddMeter(NotificationDeliveryMetrics.MeterName));
builder
    .Services.AddOptions<WorkerDatabaseOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "Worker database connection must be configured."
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<SupabaseAuthOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options => AllowedUriPolicy.IsAllowedHttp(options.Issuer, builder.Environment),
        "Supabase Auth issuer is invalid."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Audience),
        "Supabase Auth audience must be configured."
    )
    .ValidateOnStart();
// An absent allowlist keeps format-only validation; a configured one must be
// fully valid (absolute origins only) or startup fails closed.
var inviteRedirectAllowedOriginsRaw = builder.Configuration[
    "SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS"
];
var inviteRedirectAllowListConfigured = !string.IsNullOrWhiteSpace(
    inviteRedirectAllowedOriginsRaw
);
var inviteRedirectAllowedOrigins = (
    inviteRedirectAllowListConfigured
        ? inviteRedirectAllowedOriginsRaw!.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        )
        : []
).Select(candidate =>
    Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri)
    && string.IsNullOrEmpty(candidateUri.UserInfo)
    && candidateUri.GetLeftPart(UriPartial.Authority) == candidate
        ? candidate
        : null
).ToArray();

builder
    .Services.AddOptions<SupabaseAdminOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ServiceRoleKey),
        "Supabase Admin service role key must be configured."
    )
    .Validate(
        options => AllowedUriPolicy.IsAllowedHttp(options.InviteRedirectUrl, builder.Environment),
        "Supabase invitation redirect URL is invalid."
    )
    .Validate(
        _ =>
            !inviteRedirectAllowListConfigured
            || (
                inviteRedirectAllowedOrigins.Length > 0
                && inviteRedirectAllowedOrigins.All(origin => origin is not null)
            ),
        "Supabase invitation redirect allowed origins are invalid."
    )
    .Validate(
        options =>
            !inviteRedirectAllowListConfigured
            || (
                inviteRedirectAllowedOrigins.All(origin => origin is not null)
                && Uri.TryCreate(options.InviteRedirectUrl, UriKind.Absolute, out var redirectUri)
                && inviteRedirectAllowedOrigins.Contains(
                    redirectUri.GetLeftPart(UriPartial.Authority)
                )
            ),
        "Supabase invitation redirect URL origin is not allowed."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<SupabaseAuthOptions>>().Value
);
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<SupabaseAdminOptions>>().Value
);

var workerConnectionString = builder.Configuration[
    WorkerDatabaseOptions.ConnectionStringConfigurationKey
];
// Fail-fast explícito antes do fallback com string vazia: a validação
// on-start exige o mesmo valor, mas aqui a falha ocorre na composição.
if (string.IsNullOrWhiteSpace(workerConnectionString))
{
    throw new InvalidOperationException("Worker database connection must be configured.");
}

var workerDataSource = NpgsqlDataSource.Create(workerConnectionString);
builder.Services.AddSingleton(workerDataSource);
builder.Services.AddDbContext<FoundationDbContext>(
    (serviceProvider, options) =>
        options
            .UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
);
// Superfície do agente compartilhada com o ApiHost (raiz de composição do
// módulo AI): options validadas on-start, gates e provider decididos na
// composição; sem provider configurado o runner falha fechado. A etiqueta
// "ai/agent-provider" preserva a telemetria de provider deste host e o
// registro permanece classe pública tipada para a dependência inline do
// codegen do Wolverine (AlwaysUseServiceLocationFor<AgentRunner> abaixo).
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddAgentRunSurface(
    builder.Configuration,
    Environment.GetEnvironmentVariable,
    allowCredentiallessTestModel: false,
    providerModuleTag: "ai/agent-provider");
builder.Services.AddPaguaiProposal();
builder.Services.AddScoped<OwnerInvitationDeliveryIngressService>();
builder.Services.AddScoped<OwnerInvitationProviderDeliveryService>();
builder.Services.AddScoped<MemberInvitationDeliveryIngressService>();
builder.Services.AddScoped<MemberInvitationProviderDeliveryService>();
// Entrega de notificações: métricas, resolvedor e providers por canal (raiz
// de composição do módulo Notifications).
builder.Services.AddNotificationDelivery();
// Worker enfileira intents de Demandas pela função worker-only estreita
// (notifications.enqueue_demandas_notification_intent): a função genérica é
// revogada do vinx_worker (20260827120000) — mesma assinatura, troca só no
// nome. Mapeamento por TIPO (não factory): o codegen dos handlers Wolverine
// rejeita factories opacas na cadeia de dependências.
builder.Services.AddScoped<NotificationIntentService, DemandasWorkerNotificationIntentService>();
// Automação de Demandas e processamento do inbound do WhatsApp (raízes de
// composição dos módulos Demandas e Notifications).
builder.Services.AddDemandasAutomation();
builder.Services.AddInboundWhatsAppProcessing();
// Storage privado condicionado por configuração (padrão do antimalware):
// sem SUPABASE_STORAGE_* o worker sobe e o fluxo de documentos falha fechado
// (BlockedFileStorage); configurado ativa o adapter Supabase.
builder
    .Services.AddOptions<Vinx.Erp.Foundation.Files.SupabaseStorageOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            (string.IsNullOrWhiteSpace(options.ServiceRoleKey) && options.BaseUri is null)
            || (
                !string.IsNullOrWhiteSpace(options.ServiceRoleKey)
                && options.BaseUri is { IsAbsoluteUri: true } baseUri
                && (
                    baseUri.Scheme == Uri.UriSchemeHttps
                    || (builder.Environment.IsDevelopment() && baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)
                )
            ),
        "Supabase Storage options must be absent or fully configured with an HTTPS URL and service role key."
    )
    .ValidateOnStart();
builder
    .Services.AddHttpClient<Vinx.Erp.Foundation.Files.SupabaseFileStorage>(
        (serviceProvider, client) =>
        {
            var storage = serviceProvider
                .GetRequiredService<
                    IOptions<Vinx.Erp.Foundation.Files.SupabaseStorageOptions>
                >()
                .Value;
            client.BaseAddress = storage.BaseUri;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                storage.ServiceRoleKey
            );
            client.DefaultRequestHeaders.Add("apikey", storage.ServiceRoleKey);
        }
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        // O header customizado apikey não pode atravessar redirects para outra origem.
        new SocketsHttpHandler { AllowAutoRedirect = false }
    );
builder.Services.AddScoped<Vinx.Erp.Foundation.Files.FileStorage, Vinx.Erp.Foundation.Files.OptionsConfiguredFileStorage>();
// Engine antimalware condicionada por configuração: sem "MalwareScanner:ClamAvEndpoint"
// o worker permanece fail-closed (BlockedMalwareScanner); endpoint válido ativa o clamd.
builder
    .Services.AddOptions<Vinx.Erp.Foundation.Files.MalwareScannerOptions>()
    .Bind(builder.Configuration.GetSection("MalwareScanner"))
    .Validate(
        options => options.Timeout > TimeSpan.Zero
            && options.MaxScanBytes > 0
            && (string.IsNullOrWhiteSpace(options.ClamAvEndpoint)
                || Vinx.Erp.Foundation.Files.MalwareScannerOptions.TryParseEndpoint(options.ClamAvEndpoint, out _, out _)),
        "Malware scanner options must define a positive timeout and byte limit, plus a 'host:port' ClamAV endpoint when set."
    )
    .ValidateOnStart();
builder.Services.AddSingleton<Vinx.Erp.Foundation.Files.IMalwareScanner, Vinx.Erp.Foundation.Files.OptionsConfiguredMalwareScanner>();
builder
    .Services.AddOptions<MetaWhatsAppOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            // Tudo ou nada: o worker atende filas independentes e sobe sem
            // WhatsApp; configurado exige o conjunto completo e válido, e o
            // provider falha fechado por entrega quando ausente.
            (
                string.IsNullOrWhiteSpace(options.PhoneNumberId)
                && string.IsNullOrWhiteSpace(options.AccessToken)
                && string.IsNullOrWhiteSpace(options.WebAppBaseUrl)
            )
            || options.OutboundIsConfigured,
        "Meta WhatsApp outbound options are invalid."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<MetaWhatsAppOptions>>().Value
);
builder
    .Services.AddOptions<EmailProviderOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            // Tudo ou nada no espírito do Meta WhatsApp: sem SMTP o worker sobe
            // atendendo filas independentes e cada entrega de e-mail falha
            // fechada; configurado-porém-parcial é erro de deploy.
            (
                string.IsNullOrWhiteSpace(options.Host)
                && string.IsNullOrWhiteSpace(options.Username)
                && string.IsNullOrWhiteSpace(options.Password)
                && string.IsNullOrWhiteSpace(options.FromAddress)
            )
            || (
                options.OutboundIsConfigured
            ),
        "SMTP email outbound options are invalid."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<EmailProviderOptions>>().Value
);
builder
    .Services.AddHttpClient(MetaWhatsAppClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );
builder
    .Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            // A ausência mantém filas independentes operacionais. Este host
            // só envia outbound, portanto valida exclusivamente token e base;
            // o ApiHost assume o segredo de webhook quando o receber.
            (
                (
                    string.IsNullOrWhiteSpace(options.BotToken)
                    && string.Equals(
                        options.ApiBaseUrl,
                        "https://api.telegram.org",
                        StringComparison.Ordinal
                    )
                )
                || options.OutboundIsConfigured
            )
            && (string.IsNullOrWhiteSpace(options.WebAppBaseUrl)
                || options.ReviewLinksAreConfigured),
        "Telegram options are invalid."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<TelegramOptions>>().Value
);
builder
    .Services.AddHttpClient(
        TelegramClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(10)
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    )
    .RemoveAllLoggers();
builder
    .Services.AddHttpClient(
        SupabaseAdminInvitationClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(10)
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );
builder.Services.AddSingleton<WorkerDeadLetterRedactionInterceptor>();
builder.Services.AddSingleton<IDeadLetterInterceptor>(serviceProvider =>
    serviceProvider.GetRequiredService<WorkerDeadLetterRedactionInterceptor>()
);
builder.Services.AddSingleton<IHostedService, WorkerDatabasePreflight>();
builder.Services.AddSingleton<IHostedService, DemandasScheduler>();
builder.Host.UseWolverine(options =>
{
    // Em processos de teste com múltiplos hosts Wolverine, o assembly é
    // global e fixado pelo primeiro host; amarrar explicitamente evita que
    // este worker adote outro assembly (GH-3521).
    options.ApplicationAssembly = typeof(Program).Assembly;
    options.CodeGeneration.AlwaysUseServiceLocationFor<AgentRunner>();
    options.UseEntityFrameworkCoreTransactions();
    options.Discovery.DisableConventionalDiscovery(true);
    options.Discovery.IncludeType<OwnerInvitationDeliveryRequestedHandler>();
    options.Discovery.IncludeType<MemberInvitationDeliveryRequestedHandler>();
    options.Discovery.IncludeType<NotificationDeliveryRequestedHandler>();
    options.Discovery.IncludeType<PaguaiDraftCreatedHandler>();
    options.Discovery.IncludeType<InboundWhatsAppImageReceivedHandler>();
    options.Discovery.IncludeType<InboundWhatsAppTextCorrelationExpiredHandler>();
    options.Discovery.IncludeType<InboundWhatsAppDocumentReceivedHandler>();
    options.Discovery.IncludeType<InboundAgentRequestReceivedHandler>();
    options.Discovery.IncludeType<InboundTelegramTextReceivedHandler>();
    options.Discovery.IncludeType<DemandasAutomationTickHandler>();
    options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    options
        .PersistMessagesWithPostgresql(workerDataSource, schemaName: "vinx_wolverine_worker")
        .OverrideAutoCreateResources(AutoCreate.None)
        .EnableMessageTransport(transport =>
            transport.TransportSchemaName("vinx_wolverine_queues")
        );
    options.ListenToPostgresqlQueue(DeliveryQueues.OwnerInvitation).UseDurableInbox();
    options.ListenToPostgresqlQueue(DeliveryQueues.MemberInvitation).UseDurableInbox();
    options.ListenToPostgresqlQueue(DeliveryQueues.Notification).UseDurableInbox();
    options.ListenToPostgresqlQueue(PaguaiQueues.Events).UseDurableInbox();
    options.ListenToPostgresqlQueue(InboundQueues.WhatsAppInbound).UseDurableInbox();
    options.ListenToPostgresqlQueue(InboundQueues.WhatsAppInboundDocuments).UseDurableInbox();
    options.ListenToPostgresqlQueue(InboundQueues.TelegramInbound).UseDurableInbox();
    options.ListenToPostgresqlQueue(DemandasQueues.Automation).UseDurableInbox();
    // O tick é produzido E consumido por este host: sem roteamento explícito o
    // envelope cairia no endpoint local padrão e a fila durável dedicada (com
    // grants próprios) ficaria órfã.
    options
        .PublishMessage<DemandasAutomationTick>()
        .ToPostgresqlQueue(DemandasQueues.Automation);
    options
        .PublishMessage<NotificationDeliveryRequested>()
        .ToPostgresqlQueue(DeliveryQueues.Notification)
        .UseDurableOutbox();
});


var app = builder.Build();
app.MapHealthChecks("/health/live");
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { Predicate = check => check.Tags.Contains("postgres") }
);
// Alias do probe de liveness: sem execução de checks, o /health histórico
// permanece 200 mesmo sem banco configurado.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
if (args.FirstOrDefault() is "db-apply" or "db-assert")
{
    return await app.RunJasperFxCommands(args);
}

app.Run();
return 0;


internal sealed class WorkerDatabaseOptions
{
    public const string ConnectionStringConfigurationKey = "FOUNDATION_WORKER_CONNECTION";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName(ConnectionStringConfigurationKey)]
    public string ConnectionString { get; set; } = string.Empty;
}

