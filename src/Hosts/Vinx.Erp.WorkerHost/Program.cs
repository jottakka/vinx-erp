using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.WorkerHost;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddVinxLogging();
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
        options => IsAllowedSupabaseUri(options.Issuer, builder.Environment),
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
        options => IsAllowedSupabaseUri(options.InviteRedirectUrl, builder.Environment),
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
var workerDataSource = NpgsqlDataSource.Create(workerConnectionString ?? string.Empty);
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
builder.Services.AddScoped<NotificationDeliveryWorkerService>();
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
    .Services.AddHttpClient(MetaWhatsAppClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
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
    options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    options
        .PersistMessagesWithPostgresql(workerDataSource, schemaName: "vinx_wolverine_worker")
        .OverrideAutoCreateResources(AutoCreate.None)
        .EnableMessageTransport(transport =>
            transport.TransportSchemaName("vinx_wolverine_queues")
        );
    options.ListenToPostgresqlQueue("owner-invitation-delivery").UseDurableInbox();
    options.ListenToPostgresqlQueue("notification-delivery").UseDurableInbox();
});
builder.Services.AddOpenTelemetry();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
if (args.FirstOrDefault() is "db-apply" or "db-assert")
{
    return await app.RunJasperFxCommands(args);
}

app.Run();
return 0;

static bool IsAllowedSupabaseUri(string value, IHostEnvironment environment) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri)
    && string.IsNullOrEmpty(uri.UserInfo)
    && string.IsNullOrEmpty(uri.Fragment)
    && (
        uri.Scheme == Uri.UriSchemeHttps
        || (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
    );

public partial class Program;

internal sealed class WorkerDatabaseOptions
{
    public const string ConnectionStringConfigurationKey = "FOUNDATION_WORKER_CONNECTION";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName(ConnectionStringConfigurationKey)]
    public string ConnectionString { get; set; } = string.Empty;
}
