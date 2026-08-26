using System.Net;
using System.Text;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class TelegramInboundOutboxTests
{
    private const string WebhookPath = "/foundation/notifications/telegram/webhook";
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const string WebhookSecret = "telegram-integration-test-secret";

    [Fact]
    public async Task Configured_private_update_admits_one_row_and_one_opaque_envelope_through_the_public_api()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810001",
            telegramContactValidated: true
        );
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(
                """
                {"update_id":1001,"message":{"message_id":2001,"from":{"id":810001,"is_bot":false},"chat":{"id":810001,"type":"private"},"text":"private inbound test"}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(SecretHeader, WebhookSecret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inbound = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        var envelope = Assert.Single(await ReadTelegramQueueAsync(factory));
        Assert.Equal(inbound.Id, envelope.InboundMessageId);
        Assert.Equal(["inboundMessageId"], envelope.PayloadPropertyNames);
        Assert.Empty(envelope.Headers);
        AssertOpaque(
            envelope,
            "private inbound test",
            "810001",
            "1001",
            "2001",
            WebhookSecret
        );
    }

    [Fact]
    public async Task Invalid_secret_is_rejected_before_the_malformed_body_can_reach_the_database()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(SecretHeader, "wrong-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Empty(await ReadTelegramQueueAsync(factory));
    }

    [Fact]
    public async Task Empty_secret_configuration_returns_unavailable_before_header_or_body_processing()
    {
        await using var factory = await FoundationApiFactory.CreateWithEmptyTelegramWebhookSecretAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(SecretHeader, "wrong-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Replay_of_an_admitted_private_update_keeps_one_row_and_one_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810002",
            telegramContactValidated: true
        );
        using var client = factory.CreateClient();

        using (var first = await PostPrivateAsync(client, 1002, 810002, 2002, "replay test"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await PostPrivateAsync(client, 1002, 810002, 2002, "replay test"))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Single(await ReadTelegramQueueAsync(factory));
    }

    [Fact]
    public async Task Concurrent_identical_private_posts_return_safely_with_one_row_and_one_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810003",
            telegramContactValidated: true
        );
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            PostPrivateAsync(client, 1003, 810003, 2003, "concurrent replay"),
            PostPrivateAsync(client, 1003, 810003, 2003, "concurrent replay")
        );
        using var first = responses[0];
        using var second = responses[1];

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Single(await ReadTelegramQueueAsync(factory));
    }

    [Fact]
    public async Task Precommit_failure_leaves_no_row_or_envelope_and_a_retry_recovers()
    {
        var failure = new OneShotTransactionCommitFailureInterceptor();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            commandInterceptor: failure
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810004",
            telegramContactValidated: true
        );
        using var client = factory.CreateClient();
        failure.Arm();

        using (var failed = await PostPrivateAsync(client, 1004, 810004, 2004, "precommit failure"))
        {
            Assert.False(failed.IsSuccessStatusCode);
        }

        Assert.Empty(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Empty(await ReadTelegramQueueAsync(factory));

        using (var retry = await PostPrivateAsync(client, 1004, 810004, 2004, "precommit failure"))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }

        Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Single(await ReadTelegramQueueAsync(factory));
    }

    [Theory]
    [MemberData(nameof(TransportIgnoredPayloads))]
    public async Task Transport_ignored_updates_return_accepted_without_rows_or_envelopes(string payload)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Empty(await ReadTelegramQueueAsync(factory));
    }

    [Fact]
    public async Task Closed_actor_resolutions_persist_evidence_without_an_envelope_or_tenant_choice()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: null
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810006",
            telegramContactValidated: false
        );
        var inactive = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810007",
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.RevokeMembershipAsync(
            factory,
            inactive.CompanyId,
            inactive.RecipientUserId
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810008",
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810008",
            telegramContactValidated: true
        );
        using var client = factory.CreateClient();

        var cases = new[]
        {
            (ChatId: 810005L, ExpectedState: "unbound"),
            (ChatId: 810006L, ExpectedState: "unverified"),
            (ChatId: 810007L, ExpectedState: "inactive"),
            (ChatId: 810008L, ExpectedState: "ambiguous"),
        };
        foreach (var (chatId, expectedState) in cases)
        {
            using var response = await PostPrivateAsync(client, 1000 + chatId, chatId, 2000 + chatId, "closed actor");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var rows = await TelegramNotificationSeed.ReadInboundMessagesAsync(factory);
        Assert.Equal(cases.Length, rows.Count);
        foreach (var (_, expectedState) in cases)
        {
            var row = Assert.Single(rows, row => row.ResolutionState == expectedState);
            Assert.Null(row.ResolvedUserId);
            Assert.Null(row.ResolvedCompanyId);
            Assert.Null(row.ResolvedMembershipId);
        }
        Assert.Empty(await ReadTelegramQueueAsync(factory));
    }

    public static IEnumerable<object[]> TransportIgnoredPayloads()
    {
        yield return ["""{"update_id":1101,"message":{"message_id":2101,"from":{"id":810101,"is_bot":false},"chat":{"id":810101,"type":"group"},"text":"group"}}"""];
        yield return ["""{"update_id":1102,"message":{"message_id":2102,"from":{"id":810102,"is_bot":false},"chat":{"id":810102,"type":"supergroup"},"text":"supergroup"}}"""];
        yield return ["""{"update_id":1103,"message":{"message_id":2103,"from":{"id":810103,"is_bot":false},"chat":{"id":810103,"type":"channel"},"text":"channel"}}"""];
        yield return ["""{"update_id":1104,"business_message":{"message_id":2104,"from":{"id":810104,"is_bot":false},"chat":{"id":810104,"type":"private"},"text":"business"}}"""];
        yield return ["""{"update_id":1105,"message":{"message_id":2105,"from":{"id":810105,"is_bot":true},"chat":{"id":810105,"type":"private"},"text":"bot"}}"""];
        yield return ["""{"update_id":1106,"message":{"message_id":2106,"from":{"id":810106,"is_bot":false},"chat":{"id":810107,"type":"private"},"text":"mismatch"}}"""];
        yield return ["""{"update_id":1107,"message":{"message_id":2107,"from":{"id":810107,"is_bot":false},"chat":{"id":810107,"type":"private"},"photo":[{}]}}"""];
    }

    private static Task<HttpResponseMessage> PostPrivateAsync(
        HttpClient client,
        long updateId,
        long chatId,
        long messageId,
        string body
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    update_id = updateId,
                    message = new
                    {
                        message_id = messageId,
                        from = new { id = chatId, is_bot = false },
                        chat = new { id = chatId, type = "private" },
                        text = body,
                    },
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add(SecretHeader, WebhookSecret);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(SecretHeader, WebhookSecret);
        return client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<TelegramQueueEnvelope>> ReadTelegramQueueAsync(
        FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT body, row_to_json(queue)::text FROM vinx_wolverine_queues.wolverine_queue_telegram_inbound AS queue WHERE body IS NOT NULL",
            connection
        );
        await using var reader = await command.ExecuteReaderAsync();
        var envelopes = new List<TelegramQueueEnvelope>();
        while (await reader.ReadAsync())
        {
            var body = reader.GetFieldValue<byte[]>(0);
            var envelope = EnvelopeSerializer.Deserialize(body);
            using var payload = System.Text.Json.JsonDocument.Parse(envelope.Data!);
            envelopes.Add(new TelegramQueueEnvelope(
                payload.RootElement.GetProperty("inboundMessageId").GetGuid(),
                payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
                envelope.Headers.ToDictionary(header => header.Key, header => header.Value ?? string.Empty),
                body,
                reader.GetString(1)
            ));
        }

        return envelopes;
    }

    private sealed record TelegramQueueEnvelope(
        Guid InboundMessageId,
        IReadOnlyList<string> PayloadPropertyNames,
        IReadOnlyDictionary<string, string> Headers,
        byte[] RawBody,
        string RawMetadata
    );

    private static void AssertOpaque(TelegramQueueEnvelope envelope, params string[] sensitiveValues)
    {
        var headers = string.Join("\n", envelope.Headers.Select(header => $"{header.Key}={header.Value}"));
        foreach (var value in sensitiveValues)
        {
            Assert.Equal(-1, envelope.RawBody.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)));
            Assert.DoesNotContain(value, envelope.RawMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain(value, headers, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), envelope.RawMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), headers, StringComparison.Ordinal);
        }
    }
}
