using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using JasperFx;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Vinx.Erp.ApiHost.Constants;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vinx.Erp.ApiHost;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.ApiHost.Models;
using Vinx.Erp.ApiHost.OpenApi;
using Vinx.Erp.Atendimento;
using Vinx.Erp.Foundation;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Bootstrap;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Employees;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Vinx.Erp.Paguai;

var builder = WebApplication.CreateBuilder(args);
const string ProtectedRateLimitPolicy = ApiConstants.ProtectedRateLimitPolicy;
const string BrowserSessionLoginRateLimitPolicy = "browser-session-login";
const int MaximumEmployeeDirectoryRequestBodyBytes = 8 * 1024;
builder.Logging.AddVinxLogging();
builder
    .Services.AddOptions<FoundationDatabaseOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "Foundation database connection must be configured."
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<MetaWhatsAppOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            // Tudo ou nada: ausência total deixa o host subir e o webhook
            // responde 503; configurado exige o conjunto completo e válido.
            (
                string.IsNullOrWhiteSpace(options.AppSecret)
                && string.IsNullOrWhiteSpace(options.WebhookVerifyToken)
                && string.IsNullOrWhiteSpace(options.WebAppBaseUrl)
            )
            || (
                !string.IsNullOrWhiteSpace(options.AppSecret)
                && !string.IsNullOrWhiteSpace(options.WebhookVerifyToken)
                && MetaWhatsAppOptions.IsAllowedHttpsUrl(options.WebAppBaseUrl)
            ),
        "Meta WhatsApp webhook options are invalid."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<MetaWhatsAppOptions>>().Value
);
builder
    .Services.AddOptions<SupabaseStorageOptions>()
    .Bind(builder.Configuration)
    .Validate(
        options =>
            options.BaseUri is { IsAbsoluteUri: true } baseUri
            && (
                baseUri.Scheme == Uri.UriSchemeHttps
                || (
                    builder.Environment.IsDevelopment()
                    && baseUri.Scheme == Uri.UriSchemeHttp
                    && baseUri.IsLoopback
                )
            ),
        "Supabase Storage URL must use HTTPS, except loopback HTTP in Development."
    )
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ServiceRoleKey),
        "Supabase Storage service role key must be configured."
    )
    .ValidateOnStart();
builder.Services.AddDbContext<FoundationDbContext>(
    (serviceProvider, options) =>
        options
            .UseNpgsql(
                serviceProvider
                    .GetRequiredService<IOptions<FoundationDatabaseOptions>>()
                    .Value.ConnectionString
            )
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
);
var foundationConnectionString = builder.Configuration[
    FoundationDatabaseOptions.ConnectionStringConfigurationKey
];
builder.Host.UseWolverine(options =>
{
    options.UseEntityFrameworkCoreTransactions();
    if (string.IsNullOrWhiteSpace(foundationConnectionString))
    {
        return;
    }

    options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    options
        .PersistMessagesWithPostgresql(foundationConnectionString, schemaName: "vinx_wolverine")
        .OverrideAutoCreateResources(AutoCreate.None)
        .EnableMessageTransport(transport =>
            transport.TransportSchemaName("vinx_wolverine_queues")
        );
    options
        .PublishMessage<OwnerInvitationDeliveryRequested>()
        .ToPostgresqlQueue("owner-invitation-delivery")
        .UseDurableOutbox();
    options
        .PublishMessage<NotificationDeliveryRequested>()
        .ToPostgresqlQueue("notification-delivery")
        .UseDurableOutbox();
    options
        .PublishMessage<InboundWhatsAppImageReceived>()
        .ToPostgresqlQueue("whatsapp-inbound")
        .UseDurableOutbox();
});
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddScoped<ActorMembershipMapService>();
builder.Services.AddScoped<FoundationBootstrapService>();
builder.Services.AddScoped<PlatformOperatorAccessService>();
builder.Services.AddScoped<PlatformOrganizationProvisioningService>();
builder.Services.AddScoped<NotificationIntentService>();
builder.Services.AddScoped<InboundWhatsAppIngressService>();
builder.Services.AddScoped<NotificationInboxService>();
builder.Services.AddScoped<EmployeeDirectoryService>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<RevokeMembershipService>();
// Engine antimalware condicionada por configuração: sem "MalwareScanner:ClamAvEndpoint"
// o host permanece fail-closed (BlockedMalwareScanner); endpoint válido ativa o clamd.
builder
    .Services.AddOptions<MalwareScannerOptions>()
    .Bind(builder.Configuration.GetSection("MalwareScanner"))
    .Validate(
        options => options.Timeout > TimeSpan.Zero
            && options.MaxScanBytes > 0
            && (string.IsNullOrWhiteSpace(options.ClamAvEndpoint)
                || MalwareScannerOptions.TryParseEndpoint(options.ClamAvEndpoint, out _, out _)),
        "Malware scanner options must define a positive timeout and byte limit, plus a 'host:port' ClamAV endpoint when set."
    )
    .ValidateOnStart();
builder.Services.AddSingleton<IMalwareScanner>(serviceProvider =>
    MalwareScannerComposition.SelectScanner(
        serviceProvider.GetRequiredService<IOptions<MalwareScannerOptions>>().Value
    )
);
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<PaguaiDraftService>();
builder.Services.AddScoped<AtendimentoCardService>();
// Superfície do agente: options validadas on-start, gates e provider decididos
// na composição; sem provider configurado o runner falha fechado.
builder.Services.AddAgentRunSurface(builder.Configuration);
builder
    .Services.AddControllers(options => options.SuppressAsyncSuffixInActionNames = false)
    .AddJsonOptions(options =>
    {
        // Contrato estrito: campos desconhecidos no corpo são rejeitados com ModelState inválido.
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    })
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = _ =>
            new ObjectResult(new { code = "invalid_request" })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            }
    );
builder.Services.AddSupabaseJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddBrowserBffSession(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        PlatformOperatorAuthorization.PolicyName,
        policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new PlatformOperatorRequirement())
    );
    options.AddPolicy(
        PlatformOperatorAuthorization.MfaAssurancePolicyName,
        policy =>
            policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOperatorRequirement(requiresMfaAssurance: true))
    );
});
builder.Services.AddScoped<IAuthorizationHandler, PlatformOperatorAuthorizationHandler>();
builder.Services.AddSingleton<
    IAuthorizationMiddlewareResultHandler,
    PlatformOperatorAuthorizationResultHandler
>();
builder.Services.AddSingleton<IIdentityProvider>(serviceProvider => new SupabaseIdentityProvider(
    serviceProvider.GetRequiredService<IOptions<SupabaseAuthOptions>>().Value.NormalizedIssuer
));
builder.Services.AddScoped<InternalIdentityResolver>();
builder.Services.AddScoped<InternalSessionAdmissionService>();
builder.Services.AddScoped<InternalSessionAuthenticator>();
builder.Services.AddScoped<SelfSessionRevocationService>();
builder
    .Services.AddHttpClient<SupabaseFileStorage>(
        (serviceProvider, client) =>
        {
            var storage = serviceProvider
                .GetRequiredService<IOptions<SupabaseStorageOptions>>()
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
        new SocketsHttpHandler
        {
            // O header customizado apikey não pode atravessar redirects para outra origem.
            AllowAutoRedirect = false,
        }
    );
builder.Services.AddScoped<FileStorage>(serviceProvider =>
    serviceProvider.GetRequiredService<SupabaseFileStorage>()
);
builder.Services.AddSingleton<AuthorizationTelemetry>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        HttpMethods.IsPost(context.Request.Method)
        && string.Equals(
            context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName,
            BrowserSessionLoginRateLimitPolicy,
            StringComparison.Ordinal
        )
            ? RateLimitPartition.GetFixedWindowLimiter(
                "browser-session-global",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }
            )
            : RateLimitPartition.GetNoLimiter("not-browser-session")
    );
    options.AddPolicy(
        ProtectedRateLimitPolicy,
        context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.IsAuthenticated == true
                    ? context.User.FindFirst("sub")?.Value is { Length: > 0 } subject
                        ? $"authenticated:{subject}"
                        : "authenticated-without-sub"
                    : "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }
            )
    );
    options.AddPolicy(
        BrowserSessionLoginRateLimitPolicy,
        context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }
            )
    );
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())
);
builder
    .Services.AddOptions<FoundationBootstrapFeatureFlagsOptions>()
    .Bind(
        builder.Configuration.GetSection(
            FoundationBootstrapFeatureFlagsOptions.ConfigurationSectionName
        )
    )
    .Validate(
        options => options.IsValid(out _),
        "Foundation bootstrap feature flags configuration is invalid."
    )
    .ValidateOnStart();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<SecuritySchemeDocumentTransformer>();
    options.AddOperationTransformer<CorrelationResponseHeaderOperationTransformer>();
    options.AddOperationTransformer<StrictJsonRequestBodyOperationTransformer>();
    options.AddOperationTransformer<CsrfHeaderOperationTransformer>();
    options.AddOperationTransformer<SecurityRequirementsOperationTransformer>();
    options.AddSchemaTransformer<SerializerContractSchemaTransformer>();
    options.AddSchemaTransformer<AllowedValuesSchemaTransformer>();
});

var app = builder.Build();

app.UseVinxRequestCorrelation();
app.UseExceptionHandler(
    new ExceptionHandlerOptions
    {
        // Mantém o diagnóstico estruturado da exceção para o servidor sem expor detalhes ao cliente.
        SuppressDiagnosticsCallback = _ => false,
    }
);
app.UseRouting();
app.UseMiddleware<BrowserBffNoStoreMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<BrowserBffAntiforgeryMiddleware>();
app.UseMiddleware<InternalSessionGateMiddleware>();
app.UseAuthorization();
app.MapHealthChecks("/health");
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.MapBrowserBffSessionEndpoints();
app.MapAgentRunSurface();
app.MapMetaWhatsAppWebhookEndpoints();
app.MapControllers();
var foundation = app.MapGroup("/foundation")
    .RequireAuthorization()
    .RequireRateLimiting(ProtectedRateLimitPolicy);
foundation
    .MapGet(
        "/bootstrap",
        async (
            HttpContext httpContext,
            FoundationBootstrapService service,
            CancellationToken cancellationToken
        ) =>
        {
            var result = await service.GetCurrentActorAsync(
                httpContext.GetRequiredActorContext(),
                cancellationToken
            );
            return result.ToMinimalApiResult();
        }
    )
    .WithName("FoundationBootstrap")
    .Produces<FoundationBootstrapResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status429TooManyRequests)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
foundation
    .MapGet("/platform/bootstrap", () => Results.Ok(new PlatformOperatorBootstrapResponse(true)))
    .WithName("PlatformOperatorBootstrap")
    .RequireAuthorization(PlatformOperatorAuthorization.PolicyName)
    .Produces<PlatformOperatorBootstrapResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable)
    .Produces(StatusCodes.Status429TooManyRequests);
foundation
    .MapPost(
        "/platform/organizations",
        async (
            HttpRequest request,
            HttpContext httpContext,
            PlatformOrganizationProvisioningService service,
            CancellationToken cancellationToken
        ) =>
        {
            System.Text.Json.JsonDocument document;
            try
            {
                document = await System.Text.Json.JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: cancellationToken
                );
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Json(
                    new { code = "invalid_request" },
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            catch (IOException)
            {
                return Results.Json(
                    new { code = "invalid_request" },
                    statusCode: StatusCodes.Status400BadRequest
                );
            }

            using (document)
            {
                if (
                    !PlatformOrganizationHttpContract.TryParse(
                        document.RootElement,
                        out var command
                    )
                )
                {
                    return Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    );
                }

                var result = await service.ProvisionAsync(
                    httpContext.GetRequiredActorContext(),
                    command,
                    cancellationToken
                );
                return result.State switch
                {
                    PlatformOrganizationProvisioningState.Created => Results.Json(
                        new
                        {
                            result.GroupId,
                            result.CompanyId,
                            result.OwnerUserId,
                            result.OwnerMembershipId,
                            result.OwnerInvitationId,
                            invitationStatus = "pending_delivery",
                        },
                        statusCode: StatusCodes.Status201Created
                    ),
                    PlatformOrganizationProvisioningState.Replay => Results.Ok(
                        new
                        {
                            result.GroupId,
                            result.CompanyId,
                            result.OwnerUserId,
                            result.OwnerMembershipId,
                            result.OwnerInvitationId,
                            invitationStatus = "pending_delivery",
                        }
                    ),
                    PlatformOrganizationProvisioningState.IdempotencyConflict => Results.Json(
                        new { code = "idempotency_conflict" },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    PlatformOrganizationProvisioningState.Conflict => Results.Json(
                        new { code = "organization_conflict" },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    PlatformOrganizationProvisioningState.Invalid => Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    ),
                    PlatformOrganizationProvisioningState.Forbidden => Results.Json(
                        new { code = "platform_operator_required" },
                        statusCode: StatusCodes.Status403Forbidden
                    ),
                    _ => Results.Json(
                        new { code = "platform_organization_unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable
                    ),
                };
            }
        }
    )
    .WithName("PlatformOrganizationProvisioning")
    .RequireAuthorization(PlatformOperatorAuthorization.MfaAssurancePolicyName)
    .Produces(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
    .Produces(StatusCodes.Status409Conflict)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable)
    .Produces(StatusCodes.Status429TooManyRequests);
foundation.MapGet(
    "/capabilities",
    async (
        Guid? companyId,
        HttpContext httpContext,
        CapabilityMapService service,
        CancellationToken cancellationToken
    ) =>
    {
        var result = await service.GetCapabilitiesAsync(
            httpContext.GetRequiredActorContext(),
            companyId,
            cancellationToken
        );
        return result.ToMinimalApiResult();
    }
);
foundation
    .MapPost(
        "/companies/{companyId:guid}/employees",
        async (
            Guid companyId,
            HttpRequest request,
            HttpContext httpContext,
            EmployeeDirectoryService service,
            CancellationToken cancellationToken
        ) =>
        {
            try
            {
                if (!request.HasJsonContentType())
                    return Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    );
                var body = new byte[MaximumEmployeeDirectoryRequestBodyBytes + 1];
                var bodyLength = 0;
                int bytesRead;
                do
                {
                    bytesRead = await request.Body.ReadAsync(
                        body.AsMemory(bodyLength),
                        cancellationToken
                    );
                    bodyLength += bytesRead;
                } while (bytesRead != 0 && bodyLength <= MaximumEmployeeDirectoryRequestBodyBytes);

                if (bodyLength > MaximumEmployeeDirectoryRequestBodyBytes)
                    return Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    );

                using var document = System.Text.Json.JsonDocument.Parse(
                    body.AsMemory(0, bodyLength)
                );
                if (
                    !EmployeeDirectoryHttpContract.TryParse(document.RootElement, out var command)
                    || command.CompanyId != companyId
                )
                    return Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    );
                var result = await service.CreateAsync(
                    httpContext.GetRequiredActorContext(),
                    command,
                    cancellationToken
                );
                return result.State switch
                {
                    EmployeeDirectoryWriteState.Created => Results.Json(
                        new
                        {
                            result.EmployeeId,
                            result.MembershipId,
                            result.UserId,
                        },
                        statusCode: StatusCodes.Status201Created
                    ),
                    EmployeeDirectoryWriteState.Replay => Results.Ok(
                        new
                        {
                            result.EmployeeId,
                            result.MembershipId,
                            result.UserId,
                        }
                    ),
                    EmployeeDirectoryWriteState.IdempotencyConflict => Results.Json(
                        new { code = "idempotency_conflict" },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    EmployeeDirectoryWriteState.Conflict => Results.Json(
                        new { code = "employee_conflict" },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    EmployeeDirectoryWriteState.Forbidden => Results.Json(
                        new { code = "capability_denied" },
                        statusCode: StatusCodes.Status403Forbidden
                    ),
                    EmployeeDirectoryWriteState.Invalid => Results.Json(
                        new { code = "invalid_request" },
                        statusCode: StatusCodes.Status400BadRequest
                    ),
                    _ => Results.Json(
                        new { code = "employee_directory_unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable
                    ),
                };
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.Json(
                    new { code = "invalid_request" },
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
            catch (IOException)
            {
                return Results.Json(
                    new { code = "invalid_request" },
                    statusCode: StatusCodes.Status400BadRequest
                );
            }
        }
    )
    .WithName("EmployeeDirectoryCreate")
    .WithMetadata(new JsonRequestBodyContract(typeof(CreateEmployeeCommand)))
    .WithMetadata(new BrowserBffAntiforgeryRequiredMetadata(cookieAuthenticatedOnly: true))
    .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
    .Produces<EmployeeDirectoryCreatedResponse>(StatusCodes.Status201Created)
    .Produces<EmployeeDirectoryCreatedResponse>(StatusCodes.Status200OK)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status429TooManyRequests)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
foundation
    .MapGet(
        "/companies/{companyId:guid}/employees",
        async (
            Guid companyId,
            HttpContext httpContext,
            EmployeeDirectoryService service,
            CancellationToken cancellationToken
        ) =>
        {
            var result = await service.ListAsync(
                httpContext.GetRequiredActorContext(),
                companyId,
                cancellationToken
            );
            return result.ToMinimalApiResult();
        }
    )
    .WithName("EmployeeDirectoryList")
    .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
    .Produces<IReadOnlyList<EmployeeDirectoryItem>>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status403Forbidden)
    .Produces(StatusCodes.Status429TooManyRequests)
    .Produces<AuthorizationErrorResponse>(StatusCodes.Status503ServiceUnavailable);
foundation.MapGet(
    "/memberships",
    async (
        HttpContext httpContext,
        ActorMembershipMapService service,
        CancellationToken cancellationToken
    ) =>
    {
        var result = await service.GetCurrentActorAsync(
            httpContext.GetRequiredActorContext(),
            cancellationToken
        );
        return result.ToMinimalApiResult();
    }
);
foundation.MapPost(
    "/identity/sessions/revoke-all",
    async (
        HttpContext context,
        SelfSessionRevocationService service,
        CancellationToken cancellationToken
    ) =>
    {
        var result = await service.RevokeAllAsync(
            context.GetRequiredActorContext(),
            AuditSource.Web,
            cancellationToken
        );
        return
            result.State
                is SelfSessionRevocationState.Revoked
                    or SelfSessionRevocationState.NoChange
            ? Results.NoContent()
            : Results.Json(
                new { code = "identity_gate_unavailable" },
                contentType: "application/json",
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
    }
);
foundation.MapPost(
    "/files/upload-intentions",
    async (
        CreateDocumentUploadIntentCommand command,
        HttpContext httpContext,
        DocumentUploadService service,
        CancellationToken cancellationToken
    ) =>
    {
        var result = await service.CreateIntentAsync(
            httpContext.GetRequiredActorContext(),
            command,
            cancellationToken
        );
        return result.ToMinimalApiResult();
    }
);
foundation.MapPost(
    "/files/confirmations",
    async (
        ConfirmDocumentUploadCommand command,
        HttpContext httpContext,
        DocumentUploadService service,
        CancellationToken cancellationToken
    ) =>
    {
        var result = await service.ConfirmAsync(
            httpContext.GetRequiredActorContext(),
            command,
            cancellationToken
        );
        return result.ToMinimalApiResult();
    }
);
if (args.FirstOrDefault() is "db-apply" or "db-assert")
{
    return await app.RunJasperFxCommands(args);
}

app.Run();
return 0;

internal sealed class FoundationDatabaseOptions
{
    public const string ConnectionStringConfigurationKey = "FOUNDATION_RUNTIME_CONNECTION";

    [Microsoft.Extensions.Configuration.ConfigurationKeyName(ConnectionStringConfigurationKey)]
    public string ConnectionString { get; set; } = string.Empty;
}

public partial class Program;

internal static class PlatformOrganizationHttpContract
{
    private static readonly HashSet<string> Fields =
    [
        "requestId",
        "groupName",
        "companyName",
        "ownerDisplayName",
        "ownerEmail",
    ];

    public static bool TryParse(
        System.Text.Json.JsonElement body,
        out PlatformOrganizationProvisioningCommand command
    )
    {
        command = null!;
        if (body.ValueKind is not System.Text.Json.JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        if (
            values.Count != Fields.Count
            || !values["requestId"].TryGetGuid(out var requestId)
            || values["groupName"].ValueKind is not System.Text.Json.JsonValueKind.String
            || values["companyName"].ValueKind is not System.Text.Json.JsonValueKind.String
            || values["ownerDisplayName"].ValueKind is not System.Text.Json.JsonValueKind.String
            || values["ownerEmail"].ValueKind is not System.Text.Json.JsonValueKind.String
        )
        {
            return false;
        }

        command = new PlatformOrganizationProvisioningCommand(
            requestId,
            values["groupName"].GetString()!,
            values["companyName"].GetString()!,
            values["ownerDisplayName"].GetString()!,
            values["ownerEmail"].GetString()!
        );
        return true;
    }
}

internal static class EmployeeDirectoryHttpContract
{
    private static readonly HashSet<string> Fields =
    [
        "requestId",
        "companyId",
        "displayName",
        "email",
    ];

    public static bool TryParse(
        System.Text.Json.JsonElement body,
        out CreateEmployeeCommand command
    )
    {
        command = null!;
        if (body.ValueKind is not System.Text.Json.JsonValueKind.Object)
            return false;
        var values = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (!Fields.Contains(property.Name) || !values.TryAdd(property.Name, property.Value))
                return false;
        if (
            values.Count != Fields.Count
            || !values["requestId"].TryGetGuid(out var requestId)
            || !values["companyId"].TryGetGuid(out var companyId)
            || values["displayName"].ValueKind is not System.Text.Json.JsonValueKind.String
            || values["email"].ValueKind is not System.Text.Json.JsonValueKind.String
        )
            return false;
        command = new CreateEmployeeCommand(
            requestId,
            companyId,
            values["displayName"].GetString()!,
            values["email"].GetString()!
        );
        return true;
    }
}
