using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
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
builder.Services.AddSingleton<WorkerDeadLetterRedactionInterceptor>();
builder.Services.AddSingleton<IDeadLetterInterceptor>(serviceProvider =>
    serviceProvider.GetRequiredService<WorkerDeadLetterRedactionInterceptor>()
);
builder.Services.AddSingleton<IHostedService, WorkerDatabasePreflight>();
builder.Host.UseWolverine(options =>
{
    options.UseEntityFrameworkCoreTransactions();
    options.Discovery.DisableConventionalDiscovery(true);
    options.Discovery.IncludeType<OwnerInvitationDeliveryRequestedHandler>();
    options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    options
        .PersistMessagesWithPostgresql(workerDataSource, schemaName: "vinx_wolverine_worker")
        .OverrideAutoCreateResources(AutoCreate.None)
        .EnableMessageTransport(transport =>
            transport.TransportSchemaName("vinx_wolverine_queues")
        );
    options.ListenToPostgresqlQueue("owner-invitation-delivery").UseDurableInbox();
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

public partial class Program;

internal sealed class WorkerDatabaseOptions
{
    public const string ConnectionStringConfigurationKey = "FOUNDATION_WORKER_CONNECTION";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName(ConnectionStringConfigurationKey)]
    public string ConnectionString { get; set; } = string.Empty;
}
