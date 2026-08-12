using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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
builder.Logging.AddVinxLogging();
var runtimeConnection = builder.Configuration["FOUNDATION_RUNTIME_CONNECTION"];
if (string.IsNullOrWhiteSpace(runtimeConnection))
{
    throw new InvalidOperationException("FOUNDATION_RUNTIME_CONNECTION must be configured.");
}

var storageUrl = builder.Configuration["SUPABASE_STORAGE_URL"];
var storageServiceRoleKey = builder.Configuration["SUPABASE_STORAGE_SERVICE_ROLE_KEY"];
if (!Uri.TryCreate(storageUrl, UriKind.Absolute, out var storageBaseUri)
    || string.IsNullOrWhiteSpace(storageServiceRoleKey))
{
    throw new InvalidOperationException("Supabase Storage configuration must be configured.");
}

var storageOptions = new SupabaseStorageOptions(
    storageBaseUri,
    storageServiceRoleKey,
    SupabaseStorageOptions.PrivateContainerId);
storageOptions.Validate();

var authIssuer = builder.Configuration["SUPABASE_AUTH_ISSUER"];
var authAudience = builder.Configuration["SUPABASE_AUTH_AUDIENCE"];
if (!Uri.TryCreate(authIssuer, UriKind.Absolute, out var authIssuerUri)
    || string.IsNullOrWhiteSpace(authAudience))
{
    throw new InvalidOperationException("Supabase Auth configuration must be configured.");
}

var normalizedAuthIssuer = authIssuerUri.AbsoluteUri.TrimEnd('/');

builder.Services.AddDbContext<FoundationDbContext>(options => options
    .UseNpgsql(runtimeConnection)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new RowVersionInterceptor()));
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddSingleton<IIdentityProvider>(new SupabaseIdentityProvider(normalizedAuthIssuer));
builder.Services.AddScoped<InternalIdentityResolver>();
builder.Services.AddScoped<InternalSessionAdmissionService>();
builder.Services.AddScoped<SelfSessionRevocationService>();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddHttpClient<SupabaseFileStorage>(client => client.BaseAddress = storageOptions.BaseUri);
builder.Services.AddScoped<FileStorage>(serviceProvider => serviceProvider.GetRequiredService<SupabaseFileStorage>());
builder.Services.AddSingleton<AuthorizationTelemetry>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = normalizedAuthIssuer;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidIssuer = normalizedAuthIssuer;
        options.TokenValidationParameters.ValidAudience = authAudience;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidateLifetime = true;
    });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseVinxRequestCorrelation();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<InternalSessionGateMiddleware>();
app.UseAuthorization();
app.MapFoundationHealthEndpoints();
app.MapGet("/foundation/capabilities", async (
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
}).RequireAuthorization();
app.MapPost("/foundation/identity/sessions/revoke-all", async (
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
}).RequireAuthorization();
app.MapPost("/foundation/files/upload-intentions", async (
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
}).RequireAuthorization();
app.MapPost("/foundation/files/confirmations", async (
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
}).RequireAuthorization();
app.Run();

public partial class Program;
