using JasperFx;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Configuration;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Email;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.WorkerHost;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);
builder.AddVinxHostDefaults();
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
builder.Services.AddScoped<OwnerInvitationDeliveryIngressService>();
builder.Services.AddScoped<OwnerInvitationProviderDeliveryService>();
builder.Services.AddSingleton<NotificationDeliveryMetrics>();
builder.Services.AddScoped<NotificationProviderResolver>();
builder.Services.AddScoped<INotificationProvider, InAppNotificationProvider>();
builder.Services.AddScoped<INotificationProvider, MetaWhatsAppProvider>();
builder.Services.AddScoped<INotificationProvider, EmailNotificationProvider>();
builder.Services.AddScoped<NotificationDeliveryWorkerService>();
builder.Services.AddScoped<InboundWhatsAppInteractionService>();
builder.Services.AddScoped<InboundWhatsAppDocumentService>();
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
builder.Services.AddScoped<Vinx.Erp.Foundation.Files.FileStorage, WorkerFileStorage>();
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
builder.Services.AddSingleton<Vinx.Erp.Foundation.Files.IMalwareScanner, WorkerMalwareScanner>();
builder
    .Services.AddOptions<InboundWhatsAppAiOptions>()
    .Bind(builder.Configuration.GetSection(InboundWhatsAppAiOptions.SectionName))
    .Validate(
        options =>
            // Tudo-ou-nada no espírito do Meta WhatsApp: ausente mantém o
            // worker de pé (cada imagem falha fechada como ai_unconfigured);
            // presente-porém-parcial é erro de deploy e derruba o boot.
            options.ValidateConfiguration() is null,
        "Inbound WhatsApp AI profiles are invalid."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<InboundWhatsAppAiOptions>>().Value
);
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
    .Services.AddHttpClient(
        InboundWhatsAppInteractionService.TextHttpClientName,
        (serviceProvider, client) =>
            client.Timeout = serviceProvider
                .GetRequiredService<IOptions<InboundWhatsAppAiOptions>>()
                .Value.Text.Timeout
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );
builder
    .Services.AddHttpClient(
        InboundWhatsAppInteractionService.VisionHttpClientName,
        (serviceProvider, client) =>
            client.Timeout = serviceProvider
                .GetRequiredService<IOptions<InboundWhatsAppAiOptions>>()
                .Value.Vision.Timeout
    )
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
    );
builder
    .Services.AddHttpClient(
        SupabaseAdminOwnerInvitationClient.HttpClientName,
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
builder.Host.UseWolverine(options =>
{
    // Em processos de teste com múltiplos hosts Wolverine, o assembly é
    // global e fixado pelo primeiro host; amarrar explicitamente evita que
    // este worker adote outro assembly (GH-3521).
    options.ApplicationAssembly = typeof(Program).Assembly;
    options.UseEntityFrameworkCoreTransactions();
    options.Discovery.DisableConventionalDiscovery(true);
    options.Discovery.IncludeType<OwnerInvitationDeliveryRequestedHandler>();
    options.Discovery.IncludeType<NotificationDeliveryRequestedHandler>();
    options.Discovery.IncludeType<InboundWhatsAppImageReceivedHandler>();
    options.Discovery.IncludeType<InboundWhatsAppTextCorrelationExpiredHandler>();
    options.Discovery.IncludeType<InboundWhatsAppDocumentReceivedHandler>();
    options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    options
        .PersistMessagesWithPostgresql(workerDataSource, schemaName: "vinx_wolverine_worker")
        .OverrideAutoCreateResources(AutoCreate.None)
        .EnableMessageTransport(transport =>
            transport.TransportSchemaName("vinx_wolverine_queues")
        );
    options.ListenToPostgresqlQueue("owner-invitation-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("notification-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("whatsapp-inbound").UseDurableInbox();
    options.ListenToPostgresqlQueue("whatsapp-inbound-documents").UseDurableInbox();
});


var app = builder.Build();
app.MapHealthChecks("/health");
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
