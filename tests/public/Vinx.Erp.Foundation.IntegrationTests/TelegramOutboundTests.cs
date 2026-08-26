using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Notifications.Providers.Telegram;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class TelegramOutboundTests
{
    private const string TestToken = "123456:telegram-test-placeholder";
    private const string ProviderDetail = "telegram-provider-response-detail";

    [Fact]
    public async Task Eligible_delivery_posts_the_minimal_body_once_and_logs_no_secret_or_content()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = FakeTelegramBotHandler.Response(
            HttpStatusCode.OK,
            $"{{\"ok\":true,\"result\":{{\"message_id\":42}},\"description\":\"{ProviderDetail}\"}}"
        );
        var logPath = Path.Combine(Path.GetTempPath(), $"telegram-outbound-{Guid.NewGuid():N}.log");
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            debugLogPath: logPath,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "delivered",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal(1, bot.Calls);
        Assert.Equal(1, state.AttemptCount);
        var request = Assert.Single(bot.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "{\"chat_id\":\"123456789\",\"text\":\"Alerta Telegram aprovado\"}",
            request.Body
        );
        using (var payload = JsonDocument.Parse(request.Body))
        {
            Assert.Equal(["chat_id", "text"], [.. payload.RootElement.EnumerateObject().Select(property => property.Name)]);
        }

        var logs = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(TestToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"https://api.telegram.test/bot{TestToken}/sendMessage",
            logs,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(seed.TelegramAddress, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.TelegramText, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Body, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(ProviderDetail, logs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, "{}", "provider_failed", "provider_rejected")]
    [InlineData(200, "{\"ok\":false,\"description\":\"telegram-provider-response-detail\"}", "provider_failed", "provider_rejected")]
    [InlineData(408, "{}", "provider_ambiguous", "provider_ambiguous")]
    [InlineData(200, "{\"ok\":true,\"result\":{}}", "provider_ambiguous", "provider_ambiguous")]
    public async Task Provider_response_classes_preserve_terminal_state_without_false_delivery(
        int statusCode,
        string responseBody,
        string expectedStatus,
        string expectedReason
    )
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = FakeTelegramBotHandler.Response((HttpStatusCode)statusCode, responseBody);
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            expectedStatus,
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal(expectedReason, state.FailureReason);
        Assert.True(state.Resolved);
        Assert.Equal(1, bot.Calls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("transport")]
    public async Task Timeout_and_transport_are_ambiguous_with_one_external_call(string failure)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = failure == "timeout"
            ? FakeTelegramBotHandler.Failure(new OperationCanceledException())
            : FakeTelegramBotHandler.Failure(new HttpRequestException("transport placeholder"));
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_ambiguous",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_ambiguous", state.FailureReason);
        Assert.Equal(1, bot.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Transient_responses_use_the_finite_worker_retry_without_false_confirmation(int statusCode)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = FakeTelegramBotHandler.Response((HttpStatusCode)statusCode, "{\"ok\":false}");
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        await WaitForCallsAsync(bot, 4, TimeSpan.FromSeconds(90));
        var bodies = await NotificationWorkerSecurityTests.WaitDeadLetterBodyAsync(factory, 1);
        var body = Assert.Single(bodies);
        var envelope = Wolverine.Runtime.Serialization.EnvelopeSerializer.Deserialize(body);
        Assert.Empty(envelope.Headers);
        using var payload = JsonDocument.Parse(envelope.Data);
        var property = Assert.Single(payload.RootElement.EnumerateObject());
        Assert.Equal("deliveryId", property.Name);
        Assert.Equal(Guid.Empty, property.Value.GetGuid());
        var serializedEnvelope = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain(
            deliveryId.ToString("D"),
            serializedEnvelope,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain(seed.TelegramAddress, serializedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain(seed.TelegramText, serializedEnvelope, StringComparison.Ordinal);
        Assert.DoesNotContain(TestToken, serializedEnvelope, StringComparison.Ordinal);

        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("pending_provider", state.Status);
        Assert.False(state.Resolved);
        Assert.Equal(4, state.AttemptCount);
        Assert.Equal(4, bot.Calls);
    }

    [Fact]
    public async Task Disabled_provider_fails_closed_without_constructing_an_external_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(factory.Fixture.WorkerConnectionString);
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_rejected", state.FailureReason);
    }

    [Fact]
    public async Task Ambient_Telegram_settings_cannot_enable_the_default_factory_or_call_the_Bot_API()
    {
        var originalBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        var originalApiBaseUrl = Environment.GetEnvironmentVariable("TELEGRAM_API_BASE_URL");
        var originalWebhookSecret = Environment.GetEnvironmentVariable(
            "TELEGRAM_WEBHOOK_SECRET_TOKEN"
        );
        try
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", "123456:ambient-test-token");
            Environment.SetEnvironmentVariable("TELEGRAM_API_BASE_URL", "https://api.telegram.test");
            Environment.SetEnvironmentVariable(
                "TELEGRAM_WEBHOOK_SECRET_TOKEN",
                "ambient_webhook_secret"
            );

            await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
            var seed = await CreateEligibleSeedAsync(factory);
            var deliveryId = await EnqueueTelegramAsync(factory, seed);
            await using var worker = new NotificationWorkerHostFactory(
                factory.Fixture.WorkerConnectionString
            );
            using var client = worker.CreateClient();
            await using var scope = worker.Services.CreateAsyncScope();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<TelegramOptions>>().Value;

            Assert.False(options.OutboundIsConfigured);

            var handler = FindHandler<FailFastTelegramBotHandler>(
                scope.ServiceProvider
                    .GetRequiredService<IHttpMessageHandlerFactory>()
                    .CreateHandler(TelegramClient.HttpClientName)
            );
            Assert.Equal(0, handler.Calls);

            var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
                factory,
                deliveryId,
                "provider_failed",
                TimeSpan.FromSeconds(30)
            );
            Assert.Equal("provider_rejected", state.FailureReason);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", originalBotToken);
            Environment.SetEnvironmentVariable("TELEGRAM_API_BASE_URL", originalApiBaseUrl);
            Environment.SetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET_TOKEN", originalWebhookSecret);
        }
    }

    [Fact]
    public async Task Unvalidated_target_fails_closed_without_a_bot_API_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramOptedIn: true,
            telegramContactValidated: false
        );
        var bot = FakeTelegramBotHandler.Response(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":42}}");
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_rejected", state.FailureReason);
        Assert.Equal(0, bot.Calls);
    }

    [Fact]
    public async Task Redirect_response_is_not_followed_and_is_rejected_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = FakeTelegramBotHandler.Response(HttpStatusCode.Found, "", "/redirect-target");
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal(1, bot.Calls);
        Assert.Equal("provider_rejected", state.FailureReason);
    }

    [Fact]
    public async Task Host_registers_all_providers_and_hardens_the_named_telegram_client()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var factory = new NotificationWorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var providers = scope.ServiceProvider.GetServices<INotificationProvider>().ToList();
        Assert.Contains(providers, provider => provider is InAppNotificationProvider);
        Assert.Contains(providers, provider => provider is MetaWhatsAppProvider);
        Assert.Contains(providers, provider => provider is TelegramProvider);

        var telegramClient = scope.ServiceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(TelegramClient.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(10), telegramClient.Timeout);

        var primary = scope.ServiceProvider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(TelegramClient.HttpClientName);
        var sockets = FindSocketsHandler(primary);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
    }

    [Fact]
    public async Task Worker_validates_Telegram_root_configuration_at_startup()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();

        await using (var absent = new NotificationWorkerHostFactory(
            api.Fixture.WorkerConnectionString
        ))
        {
            using var client = absent.CreateClient();
        }

        await using (var tokenWithDefaultBase = new NotificationWorkerHostFactory(
            api.Fixture.WorkerConnectionString,
            telegramSettings: new Dictionary<string, string>
            {
                ["TELEGRAM_BOT_TOKEN"] = TestToken,
            }
        ))
        {
            using var client = tokenWithDefaultBase.CreateClient();
        }

        foreach (var settings in WebhookOnlyTelegramSettings())
        {
            await using var ignoredByWorker = new NotificationWorkerHostFactory(
                api.Fixture.WorkerConnectionString,
                telegramSettings: settings
            );
            using var client = ignoredByWorker.CreateClient();
        }

        foreach (var settings in InvalidTelegramSettings())
        {
            await using var invalid = new NotificationWorkerHostFactory(
                api.Fixture.WorkerConnectionString,
                telegramSettings: settings
            );
            var failure = Assert.ThrowsAny<Exception>(() => invalid.CreateClient());

            Assert.NotNull(FindException<OptionsValidationException>(failure));
            Assert.DoesNotContain(TestToken, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_worker_calls_claim_once_and_send_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await CreateEligibleSeedAsync(factory);
        var bot = FakeTelegramBotHandler.Response(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":42}}");
        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            telegramBotHandler: bot
        );
        using var client = worker.CreateClient();
        var deliveryId = await EnqueueTelegramAsync(factory, seed);

        var results = await Task.WhenAll(
            DeliverDirectAsync(worker, deliveryId, Guid.NewGuid()),
            DeliverDirectAsync(worker, deliveryId, Guid.NewGuid())
        );

        Assert.Equal(1, bot.Calls);
        Assert.Single(results, result => result.Finalized);
        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("delivered", state.Status);
        Assert.Equal(1, state.AttemptCount);
    }

    private static async Task<TelegramNotificationCompanySeed> CreateEligibleSeedAsync(
        FoundationApiFactory factory
    ) => await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
        factory,
        telegramOptedIn: true,
        telegramContactValidated: true
    );

    private static async Task<Guid> EnqueueTelegramAsync(
        FoundationApiFactory factory,
        TelegramNotificationCompanySeed seed
    )
    {
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                TelegramTemplateKey: seed.TelegramTemplateKey
            )
        );
        Assert.True(enqueued.Telegram!.Created);
        return enqueued.Telegram.DeliveryId!.Value;
    }

    private static async Task WaitForCallsAsync(
        FakeTelegramBotHandler bot,
        int expectedCalls,
        TimeSpan timeout
    )
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && bot.Calls < expectedCalls)
        {
            await Task.Delay(250);
        }
    }

    private static async Task<NotificationDeliveryResult> DeliverDirectAsync(
        NotificationWorkerHostFactory worker,
        Guid deliveryId,
        Guid correlationId
    )
    {
        await using var scope = worker.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorkerService>();
        return await service.DeliverAsync(deliveryId, correlationId, CancellationToken.None);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> InvalidTelegramSettings() =>
        [
            new Dictionary<string, string>
            {
                ["TELEGRAM_API_BASE_URL"] = "https://api.telegram.test",
            },
            new Dictionary<string, string>
            {
                ["TELEGRAM_BOT_TOKEN"] = "invalid token",
            },
            new Dictionary<string, string>
            {
                ["TELEGRAM_BOT_TOKEN"] = TestToken,
                ["TELEGRAM_API_BASE_URL"] = "http://api.telegram.test",
            },
        ];

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> WebhookOnlyTelegramSettings() =>
        [
            new Dictionary<string, string>
            {
                ["TELEGRAM_WEBHOOK_SECRET_TOKEN"] = "valid_webhook_secret",
            },
            new Dictionary<string, string>
            {
                ["TELEGRAM_BOT_TOKEN"] = TestToken,
                ["TELEGRAM_WEBHOOK_SECRET_TOKEN"] = "invalid webhook secret",
            },
        ];

    private static TException? FindException<TException>(Exception failure)
        where TException : Exception
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is TException expected)
            {
                return expected;
            }
        }

        return null;
    }

    private static SocketsHttpHandler FindSocketsHandler(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            handler = delegating.InnerHandler;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    private static THandler FindHandler<THandler>(HttpMessageHandler handler)
        where THandler : HttpMessageHandler
    {
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            if (handler is THandler expected)
            {
                return expected;
            }

            handler = delegating.InnerHandler;
        }

        return Assert.IsType<THandler>(handler);
    }

    public sealed class FakeTelegramBotHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<HttpResponseMessage>> _send;
        private readonly Lock _gate = new();
        private readonly List<CapturedTelegramRequest> _requests = [];
        private int _calls;

        private FakeTelegramBotHandler(Func<int, Task<HttpResponseMessage>> send) => _send = send;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<CapturedTelegramRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToList();
                }
            }
        }

        public static FakeTelegramBotHandler Response(
            HttpStatusCode statusCode,
            string responseBody,
            string? location = null
        ) => new(_ =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.Relative);
            }

            return Task.FromResult(response);
        });

        public static FakeTelegramBotHandler Failure(Exception exception) => new(_ =>
            Task.FromException<HttpResponseMessage>(exception)
        );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var call = Interlocked.Increment(ref _calls);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _requests.Add(new CapturedTelegramRequest(request.Method, body));
            }

            return await _send(call);
        }
    }

    public sealed record CapturedTelegramRequest(HttpMethod Method, string Body);
}
