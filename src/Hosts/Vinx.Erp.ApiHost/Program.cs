using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.ApiHost.Authentication;
using Vinx.Erp.Foundation;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddDbContext<FoundationDbContext>(options => options
    .UseNpgsql(runtimeConnection)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new RowVersionInterceptor()));
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddHttpClient<SupabaseFileStorage>(client => client.BaseAddress = storageOptions.BaseUri);
builder.Services.AddScoped<FileStorage>(serviceProvider => serviceProvider.GetRequiredService<SupabaseFileStorage>());
builder.Services.AddSingleton<AuthorizationTelemetry>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "FoundationDeferred";
    options.DefaultChallengeScheme = "FoundationDeferred";
}).AddScheme<AuthenticationSchemeOptions, DeferredAuthenticationHandler>("FoundationDeferred", _ => { });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapFoundationHealthEndpoints();
app.MapGet("/foundation/capabilities", async (
    Guid? companyId,
    ClaimsPrincipal principal,
    CapabilityMapService service,
    CancellationToken cancellationToken) =>
{
    if (!ActorContext.TryFromAuthenticatedPrincipal(principal, out var actor))
    {
        return Results.Unauthorized();
    }

    var result = await service.GetCapabilitiesAsync(actor, companyId, cancellationToken);
    return result.ToMinimalApiResult();
}).RequireAuthorization();
app.Run();

public partial class Program;
