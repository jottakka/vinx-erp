using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Telegram;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class TelegramContractTests
{
    private const string UnitTestToken = "123456:unit-test-placeholder";

    [Fact]
    public void Options_default_to_the_public_Telegram_API_and_allow_disabled_outbound()
    {
        var options = new TelegramOptions();

        Assert.Equal("https://api.telegram.org", options.ApiBaseUrl);
        Assert.False(options.OutboundIsConfigured);
        Assert.False(options.WebhookIsConfigured);
    }

    [Theory]
    [InlineData("http://api.telegram.test")]
    [InlineData("https://user:password@api.telegram.test")]
    [InlineData("https://api.telegram.test/#fragment")]
    [InlineData("not-a-uri")]
    public void Options_reject_non_allowed_API_base_urls(string apiBaseUrl)
    {
        var options = new TelegramOptions
        {
            BotToken = UnitTestToken,
            ApiBaseUrl = apiBaseUrl,
        };

        Assert.False(options.OutboundIsConfigured);
        Assert.False(TelegramOptions.IsAllowedHttpsUrl(apiBaseUrl));
    }

    [Fact]
    public void Options_require_a_token_for_outbound_but_do_not_require_a_webhook_secret()
    {
        var options = new TelegramOptions { ApiBaseUrl = "https://api.telegram.test" };

        Assert.False(options.OutboundIsConfigured);
        Assert.False(options.WebhookIsConfigured);

        options.BotToken = UnitTestToken;

        Assert.True(options.OutboundIsConfigured);
        Assert.False(options.WebhookIsConfigured);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A0_-secret")]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-abcdefgh")]
    public void Options_accept_webhook_secret_syntax(string secret)
    {
        var options = new TelegramOptions { WebhookSecretToken = secret };

        Assert.True(options.WebhookIsConfigured);
        Assert.True(TelegramOptions.IsValidWebhookSecret(secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains.dot")]
    [InlineData("contains/slash")]
    public void Options_reject_invalid_webhook_secret_syntax(string secret)
    {
        var options = new TelegramOptions { WebhookSecretToken = secret };

        Assert.False(options.WebhookIsConfigured);
        Assert.False(TelegramOptions.IsValidWebhookSecret(secret));
    }

    [Fact]
    public async Task Client_posts_only_the_bot_path_and_minimal_message_body_on_provider_acceptance()
    {
        var handler = new CapturingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"ok\":true,\"result\":{\"message_id\":42}}",
                    Encoding.UTF8,
                    "application/json"
                ),
            }
        );
        var client = BuildClient(handler);

        var result = await client.SendAsync(
            new TelegramMessage("123456789", "Approved Telegram text"),
            CancellationToken.None
        );

        Assert.Equal(NotificationProviderResultKind.Delivered, result.Kind);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(
            "https://api.telegram.test/bot123456:unit-test-placeholder/sendMessage",
            handler.Request.RequestUri!.ToString()
        );
        Assert.Equal(
            "{\"chat_id\":\"123456789\",\"text\":\"Approved Telegram text\"}",
            handler.Body
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Client_classifies_definitive_and_timeout_HTTP_responses(
        HttpStatusCode statusCode
    )
    {
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(statusCode) { Content = new StringContent("{}") }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(
            statusCode == HttpStatusCode.RequestTimeout
                ? NotificationProviderResultKind.Ambiguous
                : NotificationProviderResultKind.Rejected,
            result.Kind
        );
    }

    [Fact]
    public async Task Client_classifies_a_valid_provider_rejection_without_using_remote_details()
    {
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ok\":false,\"description\":\"secret remote detail\"}"
                    ),
                }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Rejected, result.Kind);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"ok\":true,\"result\":{\"message_id\":0}}")]
    [InlineData("{\"ok\":true,\"result\":{}}")]
    [InlineData("{\"ok\":true}")]
    public async Task Client_classifies_malformed_success_as_ambiguous(string responseBody)
    {
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody),
                }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
    }

    [Fact]
    public async Task Client_classifies_oversized_advertised_success_payload_as_ambiguous()
    {
        var content = new StringContent(
            "{\"ok\":true,\"result\":{\"message_id\":42}}",
            Encoding.UTF8,
            "application/json"
        );
        content.Headers.ContentLength = 8_193;
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
    }

    [Fact]
    public async Task Client_classifies_oversized_unadvertised_success_payload_as_ambiguous()
    {
        var payload =
            "{\"ok\":true,\"result\":{\"message_id\":42}}"
            + new string(' ', 8_193);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = null;
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
    }

    [Fact]
    public async Task Client_stops_reading_unadvertised_success_payloads_after_the_cap()
    {
        var content = new CountingContent(
            "{\"ok\":true,\"result\":{\"message_id\":42}}"
            + new string('x', 65_000)
        );
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
        // Dois chunks de 8192 bytes: o cliente encerra no instante em que o
        // limite é excedido, sem drenar o restante do corpo.
        Assert.Equal(16_384, content.Served);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Client_maps_rate_limits_and_server_errors_to_sanitized_transient_failure(
        HttpStatusCode statusCode
    )
    {
        const string body = "{\"ok\":false,\"description\":\"remote provider detail\"}";
        var client = BuildClient(
            new CapturingHandler(
                new HttpResponseMessage(statusCode) { Content = new StringContent(body) }
            )
        );

        var exception = await Assert.ThrowsAsync<NotificationProviderTransientException>(() =>
            client.SendAsync(Message(), CancellationToken.None)
        );

        Assert.DoesNotContain(UnitTestToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api.telegram.test", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("remote provider detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_classifies_transport_failure_as_ambiguous_without_leaking_details()
    {
        var client = BuildClient(
            new ThrowingHandler(
                new HttpRequestException(
                    $"request failed for https://api.telegram.test/bot{UnitTestToken}/sendMessage"
                )
            )
        );

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
    }

    [Fact]
    public async Task Client_classifies_timeout_as_ambiguous_and_makes_one_call()
    {
        var handler = new ThrowingHandler(new OperationCanceledException());
        var client = BuildClient(handler);

        var result = await client.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(NotificationProviderResultKind.Ambiguous, result.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void Mapper_preserves_an_approved_target_as_the_minimal_plain_text_payload()
    {
        var message = TelegramMapper.Map("4503599627370495", "Approved <b>text</b>");

        Assert.Equal("4503599627370495", message.ChatId);
        Assert.Equal("Approved <b>text</b>", message.Text);
        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        Assert.Equal(["chat_id", "text"], [.. payload.RootElement.EnumerateObject().Select(property => property.Name)]);
        Assert.Equal("4503599627370495", payload.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("Approved <b>text</b>", payload.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("0", "Approved Telegram text")]
    [InlineData("001", "Approved Telegram text")]
    [InlineData("-1", "Approved Telegram text")]
    [InlineData("123 456", "Approved Telegram text")]
    [InlineData("4503599627370496", "Approved Telegram text")]
    [InlineData("123456789", " ")]
    [InlineData("123456789", " Approved Telegram text")]
    [InlineData("123456789", "Approved Telegram text ")]
    public void Mapper_rejects_noncanonical_targets_and_nonblank_text_violations(
        string chatId,
        string text
    )
    {
        Assert.Throws<ArgumentException>(() => TelegramMapper.Map(chatId, text));
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\tApproved Telegram text")]
    [InlineData("Approved Telegram text\t")]
    [InlineData("\nApproved Telegram text")]
    [InlineData("Approved Telegram text\n")]
    [InlineData("\vApproved Telegram text")]
    [InlineData("Approved Telegram text\v")]
    [InlineData("\fApproved Telegram text")]
    [InlineData("Approved Telegram text\f")]
    [InlineData("\rApproved Telegram text")]
    [InlineData("Approved Telegram text\r")]
    public void Mapper_rejects_the_exact_SQL_btrim_text_violations(string text)
    {
        Assert.Throws<ArgumentException>(() => TelegramMapper.Map("123456789", text));
    }

    [Fact]
    public void Mapper_preserves_non_btrim_whitespace_as_literal_text()
    {
        const string text = "\u00A0Approved Telegram text\u00A0";

        var message = TelegramMapper.Map("123456789", text);

        Assert.Equal(text, message.Text);
    }

    [Fact]
    public void Mapper_accepts_one_supplementary_plane_code_point()
    {
        const string text = "\U0001F600";

        var message = TelegramMapper.Map("123456789", text);

        Assert.Equal(text, message.Text);
        Assert.Equal(2, message.Text.Length);
        Assert.Single(message.Text.EnumerateRunes());
    }

    [Fact]
    public void Mapper_accepts_4096_supplementary_plane_code_points()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 4096));

        var message = TelegramMapper.Map("123456789", text);

        Assert.Equal(8192, message.Text.Length);
        Assert.Equal(4096, message.Text.EnumerateRunes().Count());
    }

    [Fact]
    public void Mapper_rejects_text_over_the_Telegram_plain_text_limit()
    {
        Assert.Throws<ArgumentException>(() =>
            TelegramMapper.Map("123456789", new string('t', 4097))
        );
    }

    [Fact]
    public void Mapper_rejects_4097_supplementary_plane_code_points()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 4097));

        Assert.Throws<ArgumentException>(() => TelegramMapper.Map("123456789", text));
    }

    [Fact]
    public async Task Provider_rejects_disabled_outbound_without_resolving_a_target_or_creating_an_HTTP_client()
    {
        var httpClientFactory = new FailingHttpClientFactory();
        var provider = new TelegramProvider(
            null!,
            httpClientFactory,
            new TelegramOptions(),
            NullLogger<TelegramProvider>.Instance
        );

        var result = await provider.DeliverAsync(
            new NotificationDeliveryRequest(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None
        );

        Assert.Equal(NotificationChannel.Telegram, provider.Channel);
        Assert.Equal(NotificationProviderResultKind.Rejected, result.Kind);
        Assert.Equal(0, httpClientFactory.CreateClientCalls);
    }

    private static TelegramMessage Message() =>
        new("123456789", "Approved Telegram text");

    private static TelegramClient BuildClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new TelegramOptions
        {
            BotToken = UnitTestToken,
            ApiBaseUrl = "https://api.telegram.test",
        });

    private sealed class CountingContent(string payload) : HttpContent
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);
        private int _served;

        public int Served => Volatile.Read(ref _served);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(_payload, this));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException(
                "The counting content must be read directly by the consumer."
            );

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class CountingStream(byte[] payload, CountingContent owner) : Stream
        {
            private int _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => payload.Length;
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAt(buffer.AsMemory(offset, count));

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default
            ) => ValueTask.FromResult(ReadAt(buffer));

            private int ReadAt(Memory<byte> buffer)
            {
                var count = Math.Min(buffer.Length, payload.Length - _position);
                payload.AsSpan(_position, count).CopyTo(buffer.Span);
                _position += count;
                Interlocked.Add(ref owner._served, count);
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CallCount++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class FailingHttpClientFactory : IHttpClientFactory
    {
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            throw new InvalidOperationException("The disabled provider must not create an HTTP client.");
        }
    }
}
