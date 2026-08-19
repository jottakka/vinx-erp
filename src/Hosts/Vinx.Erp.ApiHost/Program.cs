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
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.ApiHost.Identity;
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
});
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddScoped<ActorMembershipMapService>();
builder.Services.AddScoped<FoundationBootstrapService>();
builder.Services.AddScoped<PlatformOperatorAccessService>();
builder.Services.AddScoped<PlatformOrganizationProvisioningService>();
builder.Services.AddScoped<NotificationIntentService>();
builder.Services.AddScoped<EmployeeDirectoryService>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<RevokeMembershipService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<PaguaiDraftService>();
builder.Services.AddScoped<AtendimentoCardService>();
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
    options.AddDocumentTransformer(
        (document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??=
                new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
            };
            document.Components.SecuritySchemes[BrowserBffAuthenticationExtensions.CookieScheme] =
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = BrowserBffAuthenticationExtensions.GetSessionCookieName(
                        builder.Environment
                    ),
                };
            return Task.CompletedTask;
        }
    );
    options.AddOperationTransformer(
        (operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (operation.Responses is not null)
            {
                var document =
                    context.Document
                    ?? throw new InvalidOperationException(
                        "OpenAPI response contract requires a document context."
                    );
                // Os transformers executam antes do registro final dos componentes pelo host.
                document.Workspace ??= new OpenApiWorkspace();
                document.RegisterComponents();
                foreach (var response in operation.Responses.Values)
                {
                    var concreteResponse = response switch
                    {
                        OpenApiResponse direct => direct,
                        OpenApiResponseReference reference => reference.RecursiveTarget
                            ?? throw new InvalidOperationException(
                                "OpenAPI response reference must resolve to a concrete response."
                            ),
                        _ => throw new InvalidOperationException(
                            "OpenAPI response must be a concrete response or a resolvable response reference."
                        ),
                    };

                    concreteResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    concreteResponse.Headers[RequestCorrelationMiddleware.ResponseHeaderName] =
                        new OpenApiHeader
                        {
                            Description =
                                "Referência opaca gerada pelo servidor para suporte da requisição.",
                            Required = true,
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "uuid",
                            },
                        };
                }
            }

            if (metadata?.OfType<BrowserBffSessionSignInOpenApiMetadata>().Any() == true)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Required = new HashSet<string> { "email", "password" },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["email"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                    ["password"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                    },
                                },
                                AdditionalPropertiesAllowed = false,
                            },
                        },
                    },
                };
            }

            if (metadata?.OfType<EmployeeDirectoryCreateOpenApiMetadata>().Any() == true)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(
                    new OpenApiParameter
                    {
                        Name = BrowserBffAuthenticationExtensions.AntiforgeryHeaderName,
                        In = ParameterLocation.Header,
                        Required = false,
                        Description =
                            "Obrigatório somente quando autenticado por BrowserSessionCookie; dispensado para Bearer.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    }
                );
                var opaqueResponse = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string> { "employeeId", "membershipId", "userId" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["employeeId"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "uuid",
                        },
                        ["membershipId"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "uuid",
                        },
                        ["userId"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "uuid",
                        },
                    },
                    AdditionalPropertiesAllowed = false,
                };
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Required = new HashSet<string>
                                {
                                    "requestId",
                                    "companyId",
                                    "displayName",
                                    "email",
                                },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["requestId"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "uuid",
                                    },
                                    ["companyId"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "uuid",
                                    },
                                    ["displayName"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                    },
                                    ["email"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "email",
                                    },
                                },
                                AdditionalPropertiesAllowed = false,
                            },
                        },
                    },
                };
                foreach (var status in new[] { "200", "201" })
                {
                    var response = operation.Responses![status] switch
                    {
                        OpenApiResponse direct => direct,
                        OpenApiResponseReference reference => reference.RecursiveTarget!,
                        _ => throw new InvalidOperationException("Employee response must resolve."),
                    };
                    response.Content ??= new Dictionary<string, OpenApiMediaType>();
                    response.Content["application/json"] = new OpenApiMediaType
                    {
                        Schema = opaqueResponse,
                    };
                }
            }

            if (metadata?.OfType<PaguaiJsonRequestBodyMetadata>().Any() == true)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Required = new HashSet<string>
                                {
                                    "companyId",
                                    "requestId",
                                    "title",
                                    "description",
                                },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    ["companyId"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "uuid",
                                    },
                                    ["requestId"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        Format = "uuid",
                                    },
                                    ["title"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        MaxLength = 200,
                                    },
                                    ["description"] = new OpenApiSchema
                                    {
                                        Type = JsonSchemaType.String,
                                        MaxLength = 4000,
                                    },
                                },
                                AdditionalPropertiesAllowed = false,
                            },
                        },
                    },
                };
            }

            if (
                metadata?.OfType<BrowserBffAntiforgeryRequiredMetadata>().Any() == true
                || metadata?.OfType<PaguaiAntiforgeryRequiredMetadata>().Any() == true
                || metadata?.OfType<BrowserBffCookieOnlyEndpointMetadata>().Any() == true
            )
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(
                    new OpenApiParameter
                    {
                        Name = BrowserBffAuthenticationExtensions.AntiforgeryHeaderName,
                        In = ParameterLocation.Header,
                        Required =
                            metadata?.OfType<BrowserBffCookieOnlyEndpointMetadata>().Any() == true
                            || metadata
                                ?.OfType<BrowserBffAntiforgeryRequiredMetadata>()
                                .Any(requirement => !requirement.CookieAuthenticatedOnly) == true,
                        Description =
                            "Obrigatório quando a operação autenticada usa o cookie de sessão BFF.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    }
                );
            }

            if (
                metadata?.OfType<IAllowAnonymous>().Any() != true
                && metadata?.OfType<IAuthorizeData>().Any() == true
            )
            {
                operation.Security ??= [];
                if (metadata?.OfType<BrowserBffCookieOnlyEndpointMetadata>().Any() == true)
                {
                    operation.Security.Add(
                        new OpenApiSecurityRequirement
                        {
                            [
                                new OpenApiSecuritySchemeReference(
                                    BrowserBffAuthenticationExtensions.CookieScheme,
                                    context.Document,
                                    null
                                )
                            ] = [],
                        }
                    );
                }
                else
                {
                    operation.Security.Add(
                        new OpenApiSecurityRequirement
                        {
                            [
                                new OpenApiSecuritySchemeReference("Bearer", context.Document, null)
                            ] = [],
                        }
                    );
                    operation.Security.Add(
                        new OpenApiSecurityRequirement
                        {
                            [
                                new OpenApiSecuritySchemeReference(
                                    BrowserBffAuthenticationExtensions.CookieScheme,
                                    context.Document,
                                    null
                                )
                            ] = [],
                        }
                    );
                }
            }

            return Task.CompletedTask;
        }
    );
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
    .WithMetadata(new EmployeeDirectoryCreateOpenApiMetadata())
    .WithMetadata(new BrowserBffNoStoreEndpointMetadata())
    .Produces(StatusCodes.Status201Created)
    .Produces(StatusCodes.Status200OK)
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

internal sealed class EmployeeDirectoryCreateOpenApiMetadata;
