using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.DoesNotContain("private inbound test", envelope.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("810001", envelope.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1001", envelope.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("2001", envelope.RawBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private inbound test", envelope.RawMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("810001", envelope.RawMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("1001", envelope.RawMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("2001", envelope.RawMetadata, StringComparison.Ordinal);
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
                Encoding.UTF8.GetString(body),
                reader.GetString(1)
            ));
        }

        return envelopes;
    }

    private sealed record TelegramQueueEnvelope(
        Guid InboundMessageId,
        string RawBody,
        string RawMetadata
    );
}
