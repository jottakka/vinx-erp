using System.Security.Claims;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vinx.Erp.AI;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Vinx.Erp.ApiHost.Agent;
using Vinx.Erp.Foundation.Identity;
using JsonClaimValueTypes = Microsoft.IdentityModel.JsonWebTokens.JsonClaimValueTypes;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Wolverine;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed record E2eFixtureFactoryContext(
    FoundationApiFactory Factory,
    E2eFixtureProfile Profile,
    E2eDatasetReceipt Receipt);

public sealed class FoundationApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    public const string TestAuthenticationScheme = "FoundationTest";
    public const string TelegramWebhookSecret = "telegram-integration-test-secret";

    private readonly IFoundationFixtureAccess _fixtureAccess;
    private readonly IFoundationFixtureLifecycle? _ownedFixture;
    private readonly TestPrincipalClaims _principalClaims;
    private readonly JwtTestAuthority? _jwtAuthority;
    private readonly CountingIdentityProvider? _countingIdentityProvider;
    private readonly GateDatabaseFailurePoint? _databaseFailurePoint;
    private readonly FileStorage? _testFileStorage;
    private readonly IMalwareScanner? _testMalwareScanner;
    private readonly TimeSpan? _testMalwareScanTimeout;
    private readonly IAgentModel? _testAgentModel;
    private readonly AgentRunOptions? _testAgentRunOptions;
    private readonly IInterceptor? _testInterceptor;
    private readonly HttpMessageHandler? _browserSessionProviderHandler;
    private readonly Func<ValueTask>? _disposeHostAsync;
    private readonly Func<ValueTask>? _disposeJwtAsync;
    private readonly bool _useJwtBearer;
    private readonly bool _provisionWolverineStorage;
    private readonly bool _disableWolverineForE2e;
    private readonly string _telegramWebhookSecret;

    private FoundationApiFactory(
        Guid actorUserId,
        Guid correlationId,
        TestPrincipalClaims principalClaims,
        JwtTestAuthority? jwtAuthority = null,
        CountingIdentityProvider? countingIdentityProvider = null,
        GateDatabaseFailurePoint? databaseFailurePoint = null,
        FileStorage? testFileStorage = null,
        IMalwareScanner? testMalwareScanner = null,
        TimeSpan? testMalwareScanTimeout = null,
        IAgentModel? testAgentModel = null,
        AgentRunOptions? testAgentRunOptions = null,
        HttpMessageHandler? browserSessionProviderHandler = null,
        IFoundationFixtureLifecycle? fixture = null,
        IFoundationFixtureAccess? preparedFixtureAccess = null,
        Func<ValueTask>? disposeHostAsync = null,
        Func<ValueTask>? disposeJwtAsync = null,
        bool provisionWolverineStorage = true,
        IInterceptor? testInterceptor = null,
        bool disableWolverineForE2e = false,
        string telegramWebhookSecret = TelegramWebhookSecret)
    {
        ActorUserId = actorUserId;
        CorrelationId = correlationId;
        _principalClaims = principalClaims;
        _jwtAuthority = jwtAuthority;
        _countingIdentityProvider = countingIdentityProvider;
        _databaseFailurePoint = databaseFailurePoint;
        _testFileStorage = testFileStorage;
        _testMalwareScanner = testMalwareScanner;
        _testMalwareScanTimeout = testMalwareScanTimeout;
        _testAgentModel = testAgentModel;
        _testAgentRunOptions = testAgentRunOptions;
        _testInterceptor = testInterceptor;
        _browserSessionProviderHandler = browserSessionProviderHandler;
        if (fixture is not null && preparedFixtureAccess is not null)
        {
            throw new ArgumentException("A factory cannot receive both a fixture owner and a prepared fixture access.");
        }

        if (preparedFixtureAccess is not null)
        {
            _fixtureAccess = preparedFixtureAccess;
            _ownedFixture = null;
        }
        else
        {
            _ownedFixture = fixture ?? new FoundationPostgresFixture();
            _fixtureAccess = _ownedFixture;
        }
        _disposeHostAsync = disposeHostAsync;
        _disposeJwtAsync = disposeJwtAsync;
        _useJwtBearer = jwtAuthority is not null;
        _provisionWolverineStorage = provisionWolverineStorage;
        _disableWolverineForE2e = disableWolverineForE2e;
        _telegramWebhookSecret = telegramWebhookSecret;
    }

    public Guid ActorUserId { get; }
    public Guid CorrelationId { get; }
    public FoundationPostgresFixture Fixture => _ownedFixture as FoundationPostgresFixture
        ?? throw new InvalidOperationException("The factory is configured with a test lifecycle double.");
    public int IdentityProviderCallCount => _countingIdentityProvider?.CallCount
        ?? throw new InvalidOperationException("The factory is not configured with a counting identity provider.");
    public ClaimsPrincipal? AuthenticatedJwtPrincipal { get; private set; }

    internal static string ResolveE2eContentRoot() => Path.Combine(
        WolverineStorageDeployment.FindRepositoryRoot(),
        "src",
        "Hosts",
        "Vinx.Erp.ApiHost");

    public string CreateJwtToken(JwtTokenVariation variation) =>
        (_jwtAuthority ?? throw new InvalidOperationException("The factory is not configured for JWT bearer testing."))
        .CreateToken(variation);

    public static async Task<FoundationApiFactory> CreateAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null,
        FileStorage? fileStorage = null,
        IMalwareScanner? malwareScanner = null,
        TimeSpan? malwareScanTimeout = null,
        string? aal = null)
    {
        return await CreateAsync(
            actorUserId,
            correlationId,
            fileStorage,
            malwareScanner,
            malwareScanTimeout,
            aal,
            provisionWolverineStorage: true);
    }

    internal static async Task<FoundationApiFactory> CreateWithoutWolverineStorageAsync(string? aal = null)
    {
        return await CreateAsync(aal: aal, provisionWolverineStorage: false);
    }

    public static async Task<E2eFixtureFactoryContext> CreateWithE2eProfileAsync(
        string profileName,
        CancellationToken cancellationToken,
        IAgentModel? testAgentModel = null,
        AgentRunOptions? testAgentRunOptions = null,
        FileStorage? testFileStorage = null)
    {
        if (!E2eFixtureProfileCatalog.TryGet(profileName, out var profile) || profile is null)
        {
            throw new ArgumentException("Unknown E2E fixture profile.", nameof(profileName));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = Guid.NewGuid();
        var factory = new FoundationApiFactory(
            profile.UserId,
            correlationId,
            CreateE2ePrincipalClaims(profile, correlationId),
            testFileStorage: testFileStorage,
            testAgentModel: testAgentModel,
            testAgentRunOptions: testAgentRunOptions,
            provisionWolverineStorage: false,
            disableWolverineForE2e: true);

        try
        {
            await PrepareAsync(factory);
        }
        catch (Exception exception)
        {
            throw new E2eFixtureHostException(
                E2eFixtureHostFailurePhase.FixtureStart,
                exception);
        }

        try
        {
            var receipt = await E2eFixtureSeeder.SeedAsync(factory.Fixture, cancellationToken);
            await E2eFixtureSeeder.VerifyAsync(factory.Fixture, receipt, cancellationToken);
            return new E2eFixtureFactoryContext(factory, profile, receipt);
        }
        catch (Exception primaryFailure)
        {
            var combinedFailure = await DisposeAfterFailureAsync(factory, primaryFailure);
            throw new E2eFixtureHostException(E2eFixtureHostFailurePhase.Seed, combinedFailure);
        }
    }

    internal static E2eFixtureFactoryContext CreateWithPreparedE2eProfile(
        string profileName,
        FoundationPostgresFixtureLease fixtureLease,
        E2eDatasetReceipt receipt,
        IAgentModel? testAgentModel = null,
        AgentRunOptions? testAgentRunOptions = null,
        FileStorage? testFileStorage = null)
    {
        ArgumentNullException.ThrowIfNull(fixtureLease);
        if (!E2eFixtureProfileCatalog.TryGet(profileName, out var profile) || profile is null)
        {
            throw new ArgumentException("Unknown E2E fixture profile.", nameof(profileName));
        }

        var correlationId = Guid.NewGuid();
        var factory = new FoundationApiFactory(
            profile.UserId,
            correlationId,
            CreateE2ePrincipalClaims(profile, correlationId),
            testFileStorage: testFileStorage,
            testAgentModel: testAgentModel,
            testAgentRunOptions: testAgentRunOptions,
            preparedFixtureAccess: fixtureLease,
            provisionWolverineStorage: false,
            disableWolverineForE2e: true);
        return new E2eFixtureFactoryContext(factory, profile, receipt);
    }

    private static async Task<FoundationApiFactory> CreateAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null,
        FileStorage? fileStorage = null,
        IMalwareScanner? malwareScanner = null,
        TimeSpan? malwareScanTimeout = null,
        string? aal = null,
        IAgentModel? agentModel = null,
        AgentRunOptions? agentRunOptions = null,
        bool provisionWolverineStorage = true,
        IInterceptor? commandInterceptor = null,
        string telegramWebhookSecret = TelegramWebhookSecret)
    {
        var resolvedActorUserId = actorUserId ?? Guid.NewGuid();
        var resolvedCorrelationId = correlationId ?? Guid.NewGuid();
        var factory = new FoundationApiFactory(
            resolvedActorUserId,
            resolvedCorrelationId,
            CreateTestPrincipalClaims(
                "https://project.supabase.co/auth/v1",
                "external-subject",
                "same-address@example.test",
                resolvedCorrelationId.ToString(),
                aal),
            testFileStorage: fileStorage,
            testMalwareScanner: malwareScanner,
            testMalwareScanTimeout: malwareScanTimeout,
            testAgentModel: agentModel,
            testAgentRunOptions: agentRunOptions,
            provisionWolverineStorage: provisionWolverineStorage,
            testInterceptor: commandInterceptor,
            telegramWebhookSecret: telegramWebhookSecret);
        return await PrepareAsync(factory);
    }

    public static async Task<FoundationApiFactory> CreateWithEmptyTelegramWebhookSecretAsync()
    {
        return await CreateAsync(telegramWebhookSecret: string.Empty);
    }

    public static async Task<FoundationApiFactory> CreateWithTelegramWebhookSecretAsync(
        string webhookSecret)
    {
        ArgumentNullException.ThrowIfNull(webhookSecret);
        return await CreateAsync(telegramWebhookSecret: webhookSecret);
    }

    public static async Task<FoundationApiFactory> CreateWithAuthenticatedIdentityAsync(
        Guid? actorUserId = null,
        Guid? correlationId = null,
        FileStorage? fileStorage = null,
        IMalwareScanner? malwareScanner = null,
        TimeSpan? malwareScanTimeout = null,
        string? aal = null,
        IAgentModel? agentModel = null,
        AgentRunOptions? agentRunOptions = null,
        IInterceptor? commandInterceptor = null)
    {
        var factory = await CreateAsync(
            actorUserId, correlationId, fileStorage, malwareScanner, malwareScanTimeout, aal,
            agentModel, agentRunOptions, commandInterceptor: commandInterceptor);
        return await SeedAuthenticatedActorAndReturnAsync(factory);
    }

    private static async Task<FoundationApiFactory> SeedAuthenticatedActorAndReturnAsync(
        FoundationApiFactory factory)
    {
        try
        {
            await factory.SeedAuthenticatedActorAsync();
            return factory;
        }
        catch (Exception primaryFailure)
        {
            await factory.DisposeResourcesAsync(primaryFailure);
            throw;
        }
    }

    public static async Task<FoundationApiFactory> CreateWithClaimsAsync(
        string? issuer,
        string? subject,
        string? email = "same-address@example.test",
        string? correlationIdClaim = null,
        string? aal = null)
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(issuer, subject, email, correlationIdClaim, aal));
        return await PrepareAsync(factory);
    }

    internal static async Task<FoundationApiFactory> CreateForLifecycleTestAsync(
        IFoundationFixtureLifecycle fixture,
        Func<ValueTask> disposeHostAsync,
        Func<ValueTask> disposeJwtAsync)
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            fixture: fixture,
            disposeHostAsync: disposeHostAsync,
            disposeJwtAsync: disposeJwtAsync);
        return await PrepareAsync(factory);
    }

    internal static FoundationApiFactory CreateForPreparedLifecycleTest(
        FoundationPostgresFixtureLease fixtureLease,
        Func<ValueTask> disposeHostAsync,
        Func<ValueTask> disposeJwtAsync)
    {
        return new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            preparedFixtureAccess: fixtureLease,
            disposeHostAsync: disposeHostAsync,
            disposeJwtAsync: disposeJwtAsync,
            provisionWolverineStorage: false,
            disableWolverineForE2e: true);
    }

    internal static async Task<FoundationApiFactory> CreateForSeedLifecycleTestAsync(
        IFoundationFixtureLifecycle fixture,
        Func<ValueTask> disposeHostAsync,
        Func<ValueTask> disposeJwtAsync)
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            fixture: fixture,
            disposeHostAsync: disposeHostAsync,
            disposeJwtAsync: disposeJwtAsync);
        return await SeedAuthenticatedActorAndReturnAsync(await PrepareAsync(factory));
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAsync()
    {
        return await CreateWithJwtBearerAsync(useCountingIdentityProvider: false);
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAndCountingIdentityProviderAsync()
    {
        return await CreateWithJwtBearerAsync(useCountingIdentityProvider: true);
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAndBrowserSessionProviderAsync(
        HttpMessageHandler browserSessionProviderHandler,
        bool seedAuthenticatedActor = true,
        GateDatabaseFailurePoint? databaseFailurePoint = null)
    {
        ArgumentNullException.ThrowIfNull(browserSessionProviderHandler);
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            new JwtTestAuthority(),
            databaseFailurePoint: databaseFailurePoint,
            browserSessionProviderHandler: browserSessionProviderHandler);
        return await PrepareAsync(factory, seedAuthenticatedActor);
    }

    public static async Task<FoundationApiFactory> CreateWithJwtBearerAndDatabaseFailureAsync(
        GateDatabaseFailurePoint databaseFailurePoint)
    {
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            new JwtTestAuthority(),
            databaseFailurePoint: databaseFailurePoint);
        return await PrepareAsync(factory, seedAuthenticatedActor: true);
    }

    private static async Task<FoundationApiFactory> CreateWithJwtBearerAsync(bool useCountingIdentityProvider)
    {
        var identityProvider = useCountingIdentityProvider
            ? new CountingIdentityProvider(new SupabaseIdentityProvider(JwtTestAuthority.Issuer))
            : null;
        var factory = new FoundationApiFactory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateTestPrincipalClaims(null, null, null, null),
            new JwtTestAuthority(),
            identityProvider);
        return await PrepareAsync(factory, seedAuthenticatedActor: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_testAgentModel is not null)
        {
            builder.UseSetting("AgentRun:AllowCredentiallessTestModel", "true");
        }

        if (_disableWolverineForE2e)
        {
            builder.UseContentRoot(ResolveE2eContentRoot());
        }

        builder.UseSetting("FOUNDATION_RUNTIME_CONNECTION", _fixtureAccess.RuntimeConnectionString);
        builder.UseSetting("SUPABASE_STORAGE_URL", "http://127.0.0.1:54321");
        builder.UseSetting("SUPABASE_STORAGE_SERVICE_ROLE_KEY", "test-service-role-key");
        builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.co/auth/v1");
        builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
        builder.UseSetting("SUPABASE_AUTH_PUBLISHABLE_KEY", "test-publishable-key");
        // Composição do agente em modo teste: a seção AgentRun decide na partida
        // se um provider existe (fail-closed quando ausente). O endpoint de
        // exemplo é HTTPS (loopback é rejeitado pela validação) e nunca é
        // alcançado — o fake de IAgentModel intercepta antes do HttpClient.
        if (_testAgentRunOptions is { } agentRunOptions)
        {
            if (agentRunOptions.Provider is not null)
            {
                builder.UseSetting("AgentRun:Provider", agentRunOptions.Provider);
            }

            if (agentRunOptions.Endpoint is not null)
            {
                builder.UseSetting("AgentRun:Endpoint", agentRunOptions.Endpoint.ToString());
            }

            if (agentRunOptions.Model is not null)
            {
                builder.UseSetting("AgentRun:Model", agentRunOptions.Model);
            }

            for (var index = 0; index < agentRunOptions.ApprovedHosts.Count; index++)
            {
                builder.UseSetting(
                    $"AgentRun:ApprovedHosts:{index}",
                    agentRunOptions.ApprovedHosts[index]);
            }

            if (agentRunOptions.Timeout != default)
            {
                builder.UseSetting("AgentRun:Timeout", agentRunOptions.Timeout.ToString());
            }

            if (agentRunOptions.Vision is { } vision)
            {
                if (vision.Provider is not null)
                {
                    builder.UseSetting("AgentRun:Vision:Provider", vision.Provider);
                }

                if (vision.Endpoint is not null)
                {
                    builder.UseSetting("AgentRun:Vision:Endpoint", vision.Endpoint.ToString());
                }

                if (vision.Model is not null)
                {
                    builder.UseSetting("AgentRun:Vision:Model", vision.Model);
                }

                for (var index = 0; index < vision.ApprovedHosts.Count; index++)
                {
                    builder.UseSetting(
                        $"AgentRun:Vision:ApprovedHosts:{index}",
                        vision.ApprovedHosts[index]);
                }

                if (vision.Timeout != default)
                {
                    builder.UseSetting("AgentRun:Vision:Timeout", vision.Timeout.ToString());
                }

                for (var index = 0; index < vision.AllowedClassifications.Count; index++)
                {
                    builder.UseSetting(
                        $"AgentRun:Vision:AllowedClassifications:{index}",
                        vision.AllowedClassifications[index]);
                }
            }
        }

        // Segredos de teste sem valor real: WhatsApp usa HMAC; Telegram
        // compara o header de segredo exato antes de interpretar o corpo.
        builder.UseSetting("META_WHATSAPP_PHONE_NUMBER_ID", "111111111111111");
        builder.UseSetting("META_WHATSAPP_ACCESS_TOKEN", "test-meta-access-token");
        builder.UseSetting("META_WHATSAPP_WEB_APP_BASE_URL", "https://app.example.test");
        builder.UseSetting("META_WHATSAPP_APP_SECRET", "test-meta-app-secret");
        builder.UseSetting("META_WHATSAPP_WEBHOOK_VERIFY_TOKEN", "test-meta-verify-token");
        builder.UseSetting("TELEGRAM_WEBHOOK_SECRET_TOKEN", _telegramWebhookSecret);
        builder.ConfigureTestServices(services =>
        {
            if (_disableWolverineForE2e)
            {
                services.DisableAllExternalWolverineTransports();
                services.DisableAllWolverineMessagePersistence();
            }

            services.AddTransient<IStartupFilter, AllowAnonymousTestEndpointStartupFilter>();
            services.RemoveAll<DbContextOptions<FoundationDbContext>>();
            services.AddDbContext<FoundationDbContext>((_, options) =>
            {
                options
                    .UseNpgsql(_fixtureAccess.RuntimeConnectionString)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(
                        new RowVersionInterceptor(),
                        new GateDatabaseFailureInterceptor(_databaseFailurePoint),
                        new CommitAcknowledgementFailureInterceptor());
                if (_testInterceptor is not null)
                {
                    options.AddInterceptors(_testInterceptor);
                }
            });
            if (_testFileStorage is not null)
            {
                services.RemoveAll<FileStorage>();
                services.AddSingleton(_testFileStorage);
            }
            if (_testMalwareScanner is not null)
            {
                services.RemoveAll<IMalwareScanner>();
                services.AddSingleton(_testMalwareScanner);
            }
            if (_testMalwareScanTimeout is { } malwareScanTimeout)
            {
                services.PostConfigure<MalwareScannerOptions>(options => options.Timeout = malwareScanTimeout);
            }
            if (_testAgentModel is not null)
            {
                // Padrão do IMalwareScanner: o fake substitui o model composto
                // (que exige provider configurado) sem tocar no runner.
                services.RemoveAll<IAgentModel>();
                services.AddSingleton(_testAgentModel);
            }
            if (_testAgentModel is not null && _testAgentRunOptions?.Vision is { } vision)
            {
                // O cenário conserva a fonte autorizada real. A visão é inerte
                // porque este teste termina na autorização, antes do OCR.
                services.RemoveAll<DocumentContextNormalizer>();
                services.AddScoped(serviceProvider => new DocumentContextNormalizer(
                    serviceProvider.GetRequiredService<IAuthorizedDocumentSource>(),
                    new DocumentVisionOcr(CreateInertVisionClient(vision)),
                    DocumentContextLimits.FromVisionProfile(
                        ToInertVisionProfile(vision),
                        new HashSet<string>(vision.AllowedClassifications, StringComparer.Ordinal))));
            }
            if (_useJwtBearer)
            {
                if (_countingIdentityProvider is not null)
                {
                    services.RemoveAll<IIdentityProvider>();
                    services.AddSingleton<IIdentityProvider>(_countingIdentityProvider);
                }

                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MetadataAddress = JwtTestAuthority.MetadataAddress;
                    options.Backchannel = new HttpClient(new JwtMetadataHandler(_jwtAuthority!));
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        options.MetadataAddress,
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel));
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            AuthenticatedJwtPrincipal = context.Principal;
                            return Task.CompletedTask;
                        }
                    };
                });
            }

            if (_browserSessionProviderHandler is not null)
            {
                services.AddHttpClient<SupabasePasswordSessionClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => _browserSessionProviderHandler);
            }

            if (!_useJwtBearer)
            {
                services.AddSingleton(_principalClaims);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthenticationScheme;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationScheme,
                    _ => { });
            }
        });
    }

    private static IChatClient CreateInertVisionClient(AgentRunVisionOptions vision)
    {
        var profile = ToInertVisionProfile(vision);
        profile.ApiKey = "test-only-inert";
        return new OpenAiCompatibleChatClient(profile, new HttpClient(new InertVisionHandler()));
    }

    private static OpenAiCompatibleOptions ToInertVisionProfile(AgentRunVisionOptions vision) => new()
    {
        Endpoint = vision.Endpoint,
        Model = vision.Model ?? string.Empty,
        Timeout = vision.Timeout,
        ApprovedHosts = vision.ApprovedHosts,
        AllowImageInputs = true,
        ModuleTag = "ai/agent-document-vision",
    };

    private sealed class InertVisionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("The test-only vision client must not be invoked."));
    }

    public override ValueTask DisposeAsync() => DisposeResourcesAsync(primaryFailure: null);

    private ValueTask DisposeResourcesAsync(Exception? primaryFailure) =>
        FixtureCleanup.CompleteAsync(
            primaryFailure,
            DisposeHostAsync,
            DisposeJwtAsync,
            DisposeOwnedFixtureAsync);

    private ValueTask DisposeHostAsync() => _disposeHostAsync?.Invoke() ?? base.DisposeAsync();

    private ValueTask DisposeOwnedFixtureAsync() => _ownedFixture?.DisposeAsync() ?? ValueTask.CompletedTask;

    private ValueTask DisposeJwtAsync()
    {
        if (_disposeJwtAsync is not null)
        {
            return _disposeJwtAsync();
        }

        _jwtAuthority?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<FoundationApiFactory> PrepareAsync(
        FoundationApiFactory factory,
        bool seedAuthenticatedActor = false)
    {
        try
        {
            var fixture = factory._ownedFixture
                ?? throw new InvalidOperationException("A prepared factory cannot prepare the shared fixture.");
            await fixture.StartAsync();
            await fixture.MigrateAsync();
            if (factory._provisionWolverineStorage
                && fixture is FoundationPostgresFixture postgresFixture)
            {
                await WolverineStorageDeployment.ApplyAsync(postgresFixture);
            }
            await factory.ConfigureDatabaseFailureAsync();
            if (seedAuthenticatedActor)
            {
                await factory.SeedAuthenticatedActorAsync();
            }

            return factory;
        }
        catch (Exception primaryFailure)
        {
            await factory.DisposeResourcesAsync(primaryFailure);
            throw;
        }
    }

    private async Task SeedAuthenticatedActorAsync()
    {
        await using var administrator = _fixtureAccess.CreateAdministratorDbContext();
        administrator.AddRange(
            new User(ActorUserId, "Ana", "same-address@example.test"),
            new ExternalIdentity(
                Guid.NewGuid(),
                ActorUserId,
                "https://project.supabase.co/auth/v1",
                "external-subject"));
        await administrator.SaveChangesAsync();
    }

    private async Task ConfigureDatabaseFailureAsync()
    {
        if (_databaseFailurePoint != GateDatabaseFailurePoint.SelfSessionRevocation
            || _fixtureAccess is not FoundationPostgresFixture fixture)
        {
            return;
        }

        // SelfSessionRevocationService executes its function through ADO.NET,
        // outside EF's command-interceptor pipeline. Revoke only this function
        // from the runtime role to model its actual database failure mode.
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION foundation.revoke_all_user_sessions(uuid) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static TestPrincipalClaims CreateTestPrincipalClaims(
        string? issuer,
        string? subject,
        string? email,
        string? correlationId,
        string? aal = null)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new TestPrincipalClaims(
            issuer,
            subject,
            email,
            correlationId,
            Guid.NewGuid().ToString("D"),
            issuedAt,
            issuedAt + 1,
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20),
                new AmrEntry("token_refresh", issuedAt - 5)
            ],
            aal);
    }

    private static TestPrincipalClaims CreateE2ePrincipalClaims(
        E2eFixtureProfile profile,
        Guid correlationId)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new TestPrincipalClaims(
            E2eFixtureProfileCatalog.Issuer,
            profile.Subject,
            profile.Email,
            correlationId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            issuedAt,
            issuedAt + (long)TimeSpan.FromHours(8).TotalSeconds,
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20)
            ]);
    }

    private static async Task<Exception> DisposeAfterFailureAsync(
        FoundationApiFactory factory,
        Exception primaryFailure)
    {
        try
        {
            await FixtureCleanup.CompleteAsync(primaryFailure, factory.DisposeAsync);
        }
        catch (Exception combinedFailure)
        {
            return combinedFailure;
        }

        return primaryFailure;
    }
}

public enum JwtTokenVariation
{
    Valid,
    InvalidSignature,
    InvalidIssuer,
    InvalidAudience,
    Expired,
    MissingSessionId,
    MalformedSessionId,
    MissingAmr,
    EmptyAmr,
    RefreshOnlyAmr,
    AuthenticatorAfterIssuedAt,
    IssuedAtAtOrAfterExpiresAt,
    TwoAmrEntries,
    AmrWithProviderOnlyMetadata
}

internal sealed class JwtTestAuthority : IDisposable
{
    public const string Issuer = "https://project.supabase.co/auth/v1";
    public const string Audience = "authenticated";
    public const string MetadataAddress = "https://identity.test/.well-known/openid-configuration";
    private const string JwksAddress = "https://identity.test/keys";
    private readonly RSA _signingRsa = RSA.Create(2048);
    private readonly RSA _invalidSigningRsa = RSA.Create(2048);

    public string CreateToken(JwtTokenVariation variation)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = variation switch
        {
            JwtTokenVariation.Expired => now.ToUnixTimeSeconds() - 3600,
            JwtTokenVariation.IssuedAtAtOrAfterExpiresAt => now.ToUnixTimeSeconds() + 3600,
            _ => now.ToUnixTimeSeconds() + 300
        };
        var issuedAt = variation == JwtTokenVariation.IssuedAtAtOrAfterExpiresAt
            ? expiresAt
            : now.ToUnixTimeSeconds();
        var signingKey = new RsaSecurityKey(
            variation == JwtTokenVariation.InvalidSignature ? _invalidSigningRsa : _signingRsa)
        {
            KeyId = "test-key"
        };
        var claims = new List<Claim>
        {
            new("sub", "external-subject"),
            new("iat", issuedAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        if (variation != JwtTokenVariation.MissingSessionId)
        {
            claims.Add(new Claim(
                "session_id",
                variation == JwtTokenVariation.MalformedSessionId ? "not-a-uuid" : Guid.NewGuid().ToString("D")));
        }

        claims.AddRange(CreateAmrClaims(variation, issuedAt));
        var token = new JwtSecurityToken(
            issuer: variation == JwtTokenVariation.InvalidIssuer ? "https://other.supabase.co/auth/v1" : Issuer,
            audience: variation == JwtTokenVariation.InvalidAudience ? "other-audience" : Audience,
            claims: claims,
            notBefore: variation == JwtTokenVariation.Expired ? now.AddHours(-2).UtcDateTime : now.AddMinutes(-1).UtcDateTime,
            expires: DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IEnumerable<Claim> CreateAmrClaims(JwtTokenVariation variation, long issuedAt)
    {
        if (variation == JwtTokenVariation.MissingAmr)
        {
            yield break;
        }

        if (variation == JwtTokenVariation.EmptyAmr)
        {
            yield return new Claim("amr", "[]", JsonClaimValueTypes.Json);
            yield break;
        }

        if (variation == JwtTokenVariation.AmrWithProviderOnlyMetadata)
        {
            yield return new Claim(
                "amr",
                JsonSerializer.Serialize(new
                {
                    method = "password",
                    timestamp = issuedAt - 10,
                    provider_session_metadata = "not-for-browser",
                }),
                JsonClaimValueTypes.Json);
            yield return new Claim(
                "amr",
                SerializeAmrEntry(new AmrEntry("email", issuedAt - 20)),
                JsonClaimValueTypes.Json);
            yield break;
        }

        AmrEntry[] entries = variation switch
        {
            JwtTokenVariation.RefreshOnlyAmr =>
            [new AmrEntry("token_refresh", issuedAt - 1)],
            JwtTokenVariation.AuthenticatorAfterIssuedAt =>
            [new AmrEntry("password", issuedAt + 1)],
            JwtTokenVariation.TwoAmrEntries =>
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20)
            ],
            _ =>
            [
                new AmrEntry("password", issuedAt - 10),
                new AmrEntry("email", issuedAt - 20),
                new AmrEntry("token_refresh", issuedAt - 5)
            ]
        };

        foreach (var entry in entries)
        {
            yield return new Claim("amr", SerializeAmrEntry(entry), JsonClaimValueTypes.Json);
        }
    }

    private static string SerializeAmrEntry(AmrEntry entry) => JsonSerializer.Serialize(new
    {
        method = entry.Method,
        timestamp = entry.Timestamp
    });

    public HttpResponseMessage GetResponse(Uri requestUri)
    {
        if (requestUri.AbsoluteUri == MetadataAddress)
        {
            return JsonResponse(new { issuer = Issuer, jwks_uri = JwksAddress });
        }

        if (requestUri.AbsoluteUri == JwksAddress)
        {
            var parameters = _signingRsa.ExportParameters(false);
            return JsonResponse(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = "test-key",
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent)
                    }
                }
            });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _signingRsa.Dispose();
        _invalidSigningRsa.Dispose();
    }

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
    };
}

internal sealed class JwtMetadataHandler(JwtTestAuthority authority) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(authority.GetResponse(request.RequestUri
            ?? throw new InvalidOperationException("The JWT metadata request did not have a URI.")));
}

public sealed record TestPrincipalClaims(
    string? Issuer,
    string? Subject,
    string? Email,
    string? CorrelationId,
    string SessionId,
    long IssuedAt,
    long ExpiresAt,
    IReadOnlyList<AmrEntry> Amr,
    string? Aal = null);

public sealed record AmrEntry(string Method, long Timestamp);

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TestPrincipalClaims claims) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identityClaims = new List<Claim>();
        if (claims.Issuer is not null)
        {
            identityClaims.Add(new Claim("iss", claims.Issuer));
        }

        if (claims.Subject is not null)
        {
            identityClaims.Add(new Claim("sub", claims.Subject));
        }

        if (claims.Email is not null)
        {
            identityClaims.Add(new Claim(ClaimTypes.Email, claims.Email));
        }

        if (claims.CorrelationId is not null)
        {
            identityClaims.Add(new Claim("correlation_id", claims.CorrelationId));
        }

        identityClaims.Add(new Claim("session_id", claims.SessionId));
        identityClaims.Add(new Claim("iat", claims.IssuedAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        identityClaims.Add(new Claim("exp", claims.ExpiresAt.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        if (claims.Aal is not null)
        {
            identityClaims.Add(new Claim("aal", claims.Aal));
        }
        identityClaims.AddRange(claims.Amr.Select(entry =>
            new Claim("amr", JsonSerializer.Serialize(new
            {
                method = entry.Method,
                timestamp = entry.Timestamp
            }), JsonClaimValueTypes.Json)));

        var identity = new ClaimsIdentity(identityClaims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class CountingIdentityProvider(IIdentityProvider inner) : IIdentityProvider
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof proof)
    {
        Interlocked.Increment(ref _callCount);
        return inner.TryGetExternalIdentityProof(principal, out proof);
    }
}

public enum GateDatabaseFailurePoint
{
    Resolution,
    SelfSessionRevocation,
}

internal sealed class GateDatabaseFailureInterceptor(GateDatabaseFailurePoint? failurePoint) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ThrowWhenConfigured(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowWhenConfigured(command);
        return ValueTask.FromResult(result);
    }

    private void ThrowWhenConfigured(DbCommand command)
    {
        var failsResolution = failurePoint == GateDatabaseFailurePoint.Resolution
            && command.CommandText.Contains("external_identities", StringComparison.Ordinal);
        var failsSelfSessionRevocation = failurePoint == GateDatabaseFailurePoint.SelfSessionRevocation
            && command.CommandText.Contains("revoke_all_user_sessions", StringComparison.Ordinal);
        if (failsResolution || failsSelfSessionRevocation)
        {
            throw new InvalidOperationException("Configured database failure for the identity gate test.");
        }
    }
}

internal sealed class AllowAnonymousTestEndpointStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, next) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["X-Test-Remote-IP"], out var remoteIpAddress))
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
            }

            if (context.Request.Path == "/test/identity-gate/allow-anonymous")
            {
                context.SetEndpoint(AllowAnonymousEndpoint);
            }
            else if (context.Request.Path == "/test/browser-bff/claims")
            {
                context.SetEndpoint(BrowserBffClaimsEndpoint);
            }

            return next();
        });
        next(app);
    };

    private static readonly Endpoint AllowAnonymousEndpoint = new(
        async context =>
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Anonymous");
        },
        new EndpointMetadataCollection(new AuthorizeAttribute(), new AllowAnonymousAttribute()),
        "Test allow-anonymous endpoint");

    private static readonly Endpoint BrowserBffClaimsEndpoint = new(
        async context =>
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new { amr = context.User.FindAll("amr").Select(claim => claim.Value) });
        },
        new EndpointMetadataCollection(new AuthorizeAttribute()),
        "Test browser BFF claims endpoint");
}
