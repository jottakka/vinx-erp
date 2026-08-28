using JasperFx;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.AI.Providers;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
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
var workerAgent =
    builder.Configuration.GetSection(WorkerAgentRunOptions.SectionName)
        .Get<WorkerAgentRunOptions>() ?? new WorkerAgentRunOptions();
workerAgent.Validate(Environment.GetEnvironmentVariable);
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddScoped<IAgentContextResolver, AgentContextResolver>();
builder.Services.AddScoped<PaguaiProposalService>();
builder.Services.AddSingleton<SourceAuthorizationGate>();
builder.Services.AddSingleton<AgentOutputGate>();
AgentModelSelection? workerAgentSelection = null;
if (workerAgent.Provider is not null)
{
    workerAgentSelection = new AgentModelSelection(workerAgent.Provider, workerAgent.Model!);
    var providerProfile = workerAgent.ToProviderOptions();
    builder.Services.AddHttpClient(
        WorkerAgentRunOptions.ProviderHttpClientName,
        client =>
        {
            client.BaseAddress = providerProfile.Endpoint;
            client.Timeout = providerProfile.Timeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => OpenAiCompatibleTransport.Create(providerProfile));
    builder.Services.AddScoped<IAgentModel>(serviceProvider =>
        new ChatClientAgentModel(OpenAiCompatibleChatClient.CreateForServer(
            providerProfile,
            Environment.GetEnvironmentVariable,
            serviceProvider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(WorkerAgentRunOptions.ProviderHttpClientName),
            serviceProvider.GetRequiredService<ILogger<OpenAiCompatibleChatClient>>())));
}

if (workerAgent.Vision is { } workerVision)
{
    var visionProfile = workerVision.ToProviderOptions();
    builder.Services.AddHttpClient(
        WorkerAgentRunOptions.VisionHttpClientName,
        client =>
        {
            client.BaseAddress = visionProfile.Endpoint;
            client.Timeout = visionProfile.Timeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => OpenAiCompatibleTransport.Create(visionProfile));
    builder.Services.AddScoped<IAuthorizedDocumentSource, FoundationAuthorizedDocumentSource>();
    builder.Services.AddScoped(serviceProvider =>
        new DocumentContextNormalizer(
            serviceProvider.GetRequiredService<IAuthorizedDocumentSource>(),
            new DocumentVisionOcr(OpenAiCompatibleChatClient.CreateForServer(
                visionProfile,
                Environment.GetEnvironmentVariable,
                serviceProvider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient(WorkerAgentRunOptions.VisionHttpClientName),
                serviceProvider.GetRequiredService<ILogger<OpenAiCompatibleChatClient>>())),
            DocumentContextLimits.FromVisionProfile(
                visionProfile,
                workerVision.AllowedClassifications.ToHashSet(StringComparer.Ordinal))));
}

builder.Services.AddScoped(serviceProvider => new AgentRunner(
    serviceProvider.GetRequiredService<IAgentContextResolver>(),
    serviceProvider.GetService<IAgentModel>(),
    serviceProvider.GetRequiredService<ILogger<AgentRunner>>(),
    profileId => AgentTaskProfileCatalog.CreateApprovedProfile(
        profileId,
        workerAgentSelection,
        AgentInputLimits.MainTurnMaxCharacters),
    serviceProvider.GetRequiredService<AgentOutputGate>(),
    serviceProvider.GetService<DocumentContextNormalizer>()));
builder.Services.AddScoped<AgentExecutionService>();
builder.Services.AddScoped<OwnerInvitationDeliveryIngressService>();
builder.Services.AddScoped<OwnerInvitationProviderDeliveryService>();
builder.Services.AddScoped<MemberInvitationDeliveryIngressService>();
builder.Services.AddScoped<MemberInvitationProviderDeliveryService>();
builder.Services.AddSingleton<NotificationDeliveryMetrics>();
builder.Services.AddScoped<NotificationProviderResolver>();
builder.Services.AddScoped<INotificationProvider, InAppNotificationProvider>();
builder.Services.AddScoped<INotificationProvider, MetaWhatsAppProvider>();
builder.Services.AddScoped<INotificationProvider, EmailNotificationProvider>();
builder.Services.AddScoped<INotificationProvider, TelegramProvider>();
builder.Services.AddScoped<NotificationDeliveryWorkerService>();
builder.Services.AddScoped<NotificationIntentService>();
builder.Services.AddScoped<DemandasNotificationComposer>();
builder.Services.AddScoped<DemandasAutomationScanService>();
builder.Services.AddScoped<InboundWhatsAppInteractionService>();
builder.Services.AddScoped<InboundWhatsAppDocumentService>();
builder.Services.AddScoped<InboundAttachmentMaterializer>();
builder.Services.AddScoped<InboundAgentChannelAdapter>();
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
    options.ListenToPostgresqlQueue("owner-invitation-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("member-invitation-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("notification-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("paguai-events").UseDurableInbox();
    options.ListenToPostgresqlQueue("whatsapp-inbound").UseDurableInbox();
    options.ListenToPostgresqlQueue("whatsapp-inbound-documents").UseDurableInbox();
    options.ListenToPostgresqlQueue("telegram-inbound").UseDurableInbox();
    options.ListenToPostgresqlQueue("demandas-automation").UseDurableInbox();
    // O tick é produzido E consumido por este host: sem roteamento explícito o
    // envelope cairia no endpoint local padrão e a fila durável dedicada (com
    // grants próprios) ficaria órfã.
    options
        .PublishMessage<DemandasAutomationTick>()
        .ToPostgresqlQueue("demandas-automation");
    options
        .PublishMessage<NotificationDeliveryRequested>()
        .ToPostgresqlQueue("notification-delivery")
        .UseDurableOutbox();
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

internal sealed class WorkerAgentRunOptions
{
    public const string SectionName = "AgentRun";
    public const string ProviderHttpClientName = "agent-provider";
    public const string VisionHttpClientName = "agent-document-vision";
    private const string OpenAiCompatibleProvider = "openai_compatible";

    public string? Provider { get; set; }
    public Uri? Endpoint { get; set; }
    public string? Model { get; set; }
    public IReadOnlyList<string> ApprovedHosts { get; set; } = [];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public WorkerAgentVisionOptions? Vision { get; set; }

    public void Validate(Func<string, string?> readEnvironment)
    {
        if (Provider is null && Vision is null)
        {
            return;
        }

        if (!OpenAiCompatibleChatClient.HasServerCredential(readEnvironment)
            || (Provider is not null
                && (!string.Equals(Provider, OpenAiCompatibleProvider, StringComparison.Ordinal)
                    || !ExactApprovedHostPolicy.Matches(Endpoint, ApprovedHosts)
                    || new OpenAiCompatibleOptions.Validator()
                        .ValidateForRuntimeCredential(null, ToProviderOptions()).Failed))
            || (Vision is not null
                && (!Vision.IsValid()
                    || Provider is null
                    || !string.Equals(Provider, Vision.Provider, StringComparison.Ordinal)
                    || !string.Equals(Model, Vision.Model, StringComparison.Ordinal)
                    || !ExactApprovedHostPolicy.ProfilesMatch(
                        Endpoint,
                        ApprovedHosts,
                        Vision.Endpoint,
                        Vision.ApprovedHosts))))
        {
            throw new InvalidOperationException("Agent run configuration is invalid.");
        }
    }

    public OpenAiCompatibleOptions ToProviderOptions() => new()
    {
        Endpoint = Endpoint,
        Model = Model ?? string.Empty,
        Timeout = Timeout,
        MaxInputCharacters = AgentInputLimits.MainTurnMaxCharacters,
        ApprovedHosts = ApprovedHosts,
        ModuleTag = "ai/agent-provider",
    };
}

internal sealed class WorkerAgentVisionOptions
{
    public string? Provider { get; set; }
    public Uri? Endpoint { get; set; }
    public string? Model { get; set; }
    public IReadOnlyList<string> ApprovedHosts { get; set; } = [];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);
    public IReadOnlyList<string> AllowedClassifications { get; set; } = [];

    public bool IsValid() =>
        string.Equals(Provider, "openai_compatible", StringComparison.Ordinal)
        && AllowedClassifications.Count > 0
        && AllowedClassifications.All(value => !string.IsNullOrWhiteSpace(value))
        && ExactApprovedHostPolicy.Matches(Endpoint, ApprovedHosts)
        && !new OpenAiCompatibleOptions.Validator()
            .ValidateForRuntimeCredential(null, ToProviderOptions()).Failed;

    public OpenAiCompatibleOptions ToProviderOptions() => new()
    {
        Endpoint = Endpoint,
        Model = Model ?? string.Empty,
        Timeout = Timeout,
        ApprovedHosts = ApprovedHosts,
        AllowImageInputs = true,
        ModuleTag = "ai/agent-document-vision",
    };
}
