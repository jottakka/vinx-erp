using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Rebaseline do inbound após remover a autoridade direta de OCR/modelo do
/// adapter Meta: admissão e correlação publicam somente o envelope opaco comum.
/// Barreiras de reply continuam cobertas por InboundWhatsAppReplyStateFunctionTests.
/// </summary>
[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    [Theory]
    [InlineData("text", true)]
    [InlineData("image", false)]
    [InlineData("document", false)]
    public async Task Admitted_message_publishes_only_the_opaque_agent_envelope(
        string messageType,
        bool scheduled)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var messageId = $"wamid.agent.{messageType}.{Guid.NewGuid():N}";

        using var response = await PostSignedAsync(
            client,
            Payload(messageType, messageId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = Assert.Single(await ReadInboundQueueAsync(factory));
        Assert.EndsWith(
            "InboundAgentRequestReceived",
            envelope.MessageType,
            StringComparison.Ordinal);
        Assert.Equal(scheduled, envelope.Scheduled);
        Assert.Equal(
            ["channel", "inboundMessageId"],
            envelope.PropertyNames);
    }

    [Fact]
    public async Task Unbound_sender_creates_no_agent_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            Payload("text", $"wamid.agent.unbound.{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await ReadInboundQueueAsync(factory));
        Assert.Equal(
            "unbound",
            Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory)).ResolutionState);
    }

    [Fact]
    public async Task Webhook_replay_publishes_one_agent_envelope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var payload = Payload("document", $"wamid.agent.replay.{Guid.NewGuid():N}");

        using (var first = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var replay = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        Assert.Single(await ReadInboundQueueAsync(factory));
        Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Text_image_pair_keeps_correlation_and_both_envelopes_opaque(bool textFirst)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAdmittedSenderAsync(factory);
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var text = Payload("text", $"wamid.agent.text.{suffix}");
        var image = Payload("image_without_caption", $"wamid.agent.image.{suffix}");

        foreach (var payload in textFirst ? new[] { text, image } : new[] { image, text })
        {
            using var response = await PostSignedAsync(client, payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var envelopes = await ReadInboundQueueAsync(factory);
        Assert.Equal(2, envelopes.Count);
        Assert.All(envelopes, envelope =>
        {
            Assert.EndsWith(
                "InboundAgentRequestReceived",
                envelope.MessageType,
                StringComparison.Ordinal);
            Assert.Equal(["channel", "inboundMessageId"], envelope.PropertyNames);
        });

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.inbound_whatsapp_messages WHERE message_type = 'image' AND correlated_text_id IS NOT NULL",
            administrator);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task SeedAdmittedSenderAsync(FoundationApiFactory factory)
    {
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true);
    }

    private static async Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", $"sha256={signature}");
        return await client.SendAsync(request);
    }

    private static string Payload(string messageType, string messageId)
    {
        var type = messageType == "image_without_caption" ? "image" : messageType;
        object content = messageType switch
        {
            "text" => new { body = "agent request" },
            "image" => new { id = "media-agent", caption = "agent attachment" },
            "image_without_caption" => new { id = "media-agent" },
            "document" => new { id = "media-agent" },
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null),
        };
        var message = new Dictionary<string, object?>
        {
            ["id"] = messageId,
            ["from"] = SenderWaId,
            ["type"] = type,
            [type] = content,
        };
        return JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new { phone_number_id = ChannelPhoneNumberId },
                                messages = new[] { message },
                            },
                        },
                    },
                },
            },
        });
    }

    private sealed record QueueEnvelope(
        string MessageType,
        bool Scheduled,
        string[] PropertyNames);

    private static async Task<IReadOnlyList<QueueEnvelope>> ReadInboundQueueAsync(
        FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT body, false
            FROM vinx_wolverine_queues.wolverine_queue_whatsapp_inbound
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, true
            FROM vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_scheduled
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, false
            FROM vinx_wolverine.wolverine_outgoing_envelopes
            WHERE body IS NOT NULL
            UNION ALL
            SELECT body, execution_time IS NOT NULL
            FROM vinx_wolverine.wolverine_incoming_envelopes
            WHERE body IS NOT NULL
              AND status = 'Scheduled'
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<QueueEnvelope>();
        while (await reader.ReadAsync())
        {
            var envelope = EnvelopeSerializer.Deserialize(reader.GetFieldValue<byte[]>(0));
            using var payload = JsonDocument.Parse(envelope.Data!);
            result.Add(new QueueEnvelope(
                envelope.MessageType!,
                reader.GetBoolean(1) || envelope.ScheduledTime.HasValue,
                payload.RootElement.EnumerateObject()
                    .Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .ToArray()));
        }

        return result;
    }
}
