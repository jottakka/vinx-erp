using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;

var builder = WebApplication.CreateBuilder(args);
const string ProtectedRateLimitPolicy = "foundation";
builder.Logging.AddVinxLogging();
builder.Services.AddOptions<FoundationDatabaseOptions>()
    .Bind(builder.Configuration)
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Foundation database connection must be configured.")
    .ValidateOnStart();
builder.Services.AddOptions<SupabaseStorageOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options => options.BaseUri is { IsAbsoluteUri: true } baseUri
            && (baseUri.Scheme == Uri.UriSchemeHttps
                || (builder.Environment.IsDevelopment()
                    && baseUri.Scheme == Uri.UriSchemeHttp
                    && baseUri.IsLoopback)),
        "Supabase Storage URL must use HTTPS, except loopback HTTP in Development.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceRoleKey), "Supabase Storage service role key must be configured.")
    .ValidateOnStart();
builder.Services.AddDbContext<FoundationDbContext>((serviceProvider, options) => options
    .UseNpgsql(serviceProvider.GetRequiredService<IOptions<FoundationDatabaseOptions>>().Value.ConnectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new RowVersionInterceptor()));
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddScoped<ActorMembershipMapService>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<RevokeMembershipService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddSupabaseJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IIdentityProvider>(serviceProvider =>
    new SupabaseIdentityProvider(
        serviceProvider.GetRequiredService<IOptions<SupabaseAuthOptions>>().Value.NormalizedIssuer));
builder.Services.AddScoped<InternalIdentityResolver>();
builder.Services.AddScoped<InternalSessionAdmissionService>();
builder.Services.AddScoped<SelfSessionRevocationService>();
builder.Services.AddHttpClient<SupabaseFileStorage>((serviceProvider, client) =>
{
    var storage = serviceProvider.GetRequiredService<IOptions<SupabaseStorageOptions>>().Value;
    client.BaseAddress = storage.BaseUri;
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", storage.ServiceRoleKey);
    client.DefaultRequestHeaders.Add("apikey", storage.ServiceRoleKey);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // O header customizado apikey não pode atravessar redirects para outra origem.
    AllowAutoRedirect = false
});
builder.Services.AddScoped<FileStorage>(serviceProvider => serviceProvider.GetRequiredService<SupabaseFileStorage>());
builder.Services.AddSingleton<AuthorizationTelemetry>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ProtectedRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseVinxRequestCorrelation();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Mantém o diagnóstico estruturado da exceção para o servidor sem expor detalhes ao cliente.
    SuppressDiagnosticsCallback = _ => false
});
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<InternalSessionGateMiddleware>();
app.UseAuthorization();
app.MapHealthChecks("/health");
var foundation = app.MapGroup("/foundation")
    .RequireAuthorization()
    .RequireRateLimiting(ProtectedRateLimitPolicy);
foundation.MapGet("/capabilities", async (
    Guid? companyId,
    HttpContext httpContext,
    CapabilityMapService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetCapabilitiesAsync(
        httpContext.GetRequiredActorContext(),
        companyId,
        cancellationToken);
    return result.ToMinimalApiResult();
});
foundation.MapGet("/memberships", async (
    HttpContext httpContext,
    ActorMembershipMapService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetCurrentActorAsync(
        httpContext.GetRequiredActorContext(),
        cancellationToken);
    return result.ToMinimalApiResult();
});
foundation.MapPost("/identity/sessions/revoke-all", async (
    HttpContext context,
    SelfSessionRevocationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.RevokeAllAsync(
        context.GetRequiredActorContext(), AuditSource.Web, cancellationToken);
    return result.State is SelfSessionRevocationState.Revoked or SelfSessionRevocationState.NoChange
        ? Results.NoContent()
        : Results.Json(
            new { code = "identity_gate_unavailable" },
            contentType: "application/json",
            statusCode: StatusCodes.Status503ServiceUnavailable);
});
foundation.MapPost("/files/upload-intentions", async (
    CreateDocumentUploadIntentCommand command,
    HttpContext httpContext,
    DocumentUploadService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.CreateIntentAsync(
        httpContext.GetRequiredActorContext(),
        command,
        cancellationToken);
    return result.ToMinimalApiResult();
});
foundation.MapPost("/files/confirmations", async (
    ConfirmDocumentUploadCommand command,
    HttpContext httpContext,
    DocumentUploadService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ConfirmAsync(
        httpContext.GetRequiredActorContext(),
        command,
        cancellationToken);
    return result.ToMinimalApiResult();
});
app.Run();

internal sealed class FoundationDatabaseOptions
{
    public const string ConnectionStringConfigurationKey = "FOUNDATION_RUNTIME_CONNECTION";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName(ConnectionStringConfigurationKey)]
    public string ConnectionString { get; set; } = string.Empty;
}

public partial class Program;
