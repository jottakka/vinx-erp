using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Vinx.Erp.ApiHost.Identity;
using Vinx.Erp.Foundation;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Bootstrap;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Platform;
using Vinx.Erp.Paguai;

var builder = WebApplication.CreateBuilder(args);
const string ProtectedRateLimitPolicy = "foundation";
const string BrowserSessionLoginRateLimitPolicy = "browser-session-login";
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
builder.Services.AddScoped<AuditAppender>();
builder.Services.AddScoped<CapabilityMapService>();
builder.Services.AddScoped<AuthorizedCompanySetResolver>();
builder.Services.AddScoped<ActorMembershipMapService>();
builder.Services.AddScoped<FoundationBootstrapService>();
builder.Services.AddScoped<PlatformOperatorAccessService>();
builder.Services.AddScoped<PlatformOrganizationProvisioningService>();
builder.Services.AddScoped<CapabilityEvaluator>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<RevokeMembershipService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<PaguaiDraftService>();
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
                context.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous",
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
                        Required = true,
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
app.MapPaguaiEndpoints(ProtectedRateLimitPolicy);
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
app.Run();

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
