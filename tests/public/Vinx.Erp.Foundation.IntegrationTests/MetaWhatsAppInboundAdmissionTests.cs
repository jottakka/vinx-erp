using System.Net;
using System.Text;
using Npgsql;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD da admissão de mídia inbound (imagem/documento) com resolução de
/// identidade fechada (#272 PR1): somente remetentes com exatamente um
/// usuário, uma empresa e uma membership ativa avançam como admitidos.
/// </summary>
[Collection("notification-worker")]
public sealed class MetaWhatsAppInboundAdmissionTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    [Fact]
    public async Task Validated_document_sender_is_admitted_without_processing()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            DocumentPayload("wamid.in.doc.1", SenderWaId, "media.doc.1", null)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("wamid.in.doc.1", row.MessageId);
        Assert.Equal(ChannelPhoneNumberId, row.PhoneNumberId);
        Assert.Equal("document", row.MessageType);
        Assert.Equal("media.doc.1", row.MediaId);
        Assert.Equal("admitted", row.ResolutionState);
        Assert.Equal(seed.RecipientUserId, row.ResolvedUserId);
        Assert.Equal(seed.CompanyId, row.ResolvedCompanyId);
        Assert.NotNull(row.ResolvedMembershipId);
        // PR1 não processa documentos: nenhum download, OCR ou rascunho;
        // a linha permanece pendente como admissão durável.
        Assert.Equal("pending", row.Status);
        Assert.Null(row.ReplyMessageId);
        Assert.Null(row.FailureReason);
    }

    [Fact]
    public async Task Unknown_message_type_is_ignored_without_admission()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(client, AudioPayload());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
    }

    [Fact]
    public async Task Missing_channel_metadata_or_identifiers_do_not_advance()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        var withoutChannel = ImagePayload(
            "wamid.in.nochannel",
            SenderWaId,
            "1940",
            null,
            includeChannel: false
        );
        var withoutMessageId = ImagePayload("", SenderWaId, "1941", null);
        var withoutSender = ImagePayloadWithoutSender("wamid.in.nosender", "1942");

        foreach (var payload in new[] { withoutChannel, withoutMessageId, withoutSender })
        {
            using var response = await PostSignedAsync(client, payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
    }

    [Fact]
    public async Task Unknown_channel_phone_number_id_is_not_admitted()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        using var client = factory.CreateClient();

        var payload = ImagePayload("wamid.in.otherchannel", SenderWaId, "1943", null)
            .Replace(ChannelPhoneNumberId, "999999999999", StringComparison.Ordinal);
        using var response = await PostSignedAsync(client, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
    }

    [Fact]
    public async Task Sender_without_contact_is_unbound()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.unbound", SenderWaId, "1944", null)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("unbound", row.ResolutionState);
        Assert.Null(row.ResolvedUserId);
        Assert.Null(row.ResolvedCompanyId);
        Assert.Null(row.ResolvedMembershipId);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    public async Task Unvalidated_contact_is_unverified()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: false
        );
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.unverified", SenderWaId, "1945", null)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("unverified", row.ResolutionState);
        Assert.Null(row.ResolvedUserId);
        Assert.Equal("pending", row.Status);
    }

    [Fact]
    public async Task Contact_in_multiple_companies_is_ambiguous()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var first = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            first.CompanyId,
            first.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        var second = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            second.CompanyId,
            second.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        using var client = factory.CreateClient();

        using var response = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.ambiguous", SenderWaId, "1946", null)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Nunca escolher automaticamente entre as duas empresas.
        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("ambiguous", row.ResolutionState);
        Assert.Null(row.ResolvedCompanyId);
        Assert.Null(row.ResolvedMembershipId);
    }

    [Fact]
    public async Task Revoked_membership_is_inactive()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var revoke = new NpgsqlCommand(
            """
            UPDATE foundation.memberships
                SET deleted_at = clock_timestamp(), deleted_by_user_id = @actor
                WHERE company_id = @company_id AND user_id = @recipient
            """,
            administrator
        );
        revoke.Parameters.AddWithValue("actor", factory.ActorUserId);
        revoke.Parameters.AddWithValue("company_id", seed.CompanyId);
        revoke.Parameters.AddWithValue("recipient", seed.RecipientUserId);
        await revoke.ExecuteNonQueryAsync();

        using var client = factory.CreateClient();
        using var response = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.inactive", SenderWaId, "1947", null)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("inactive", row.ResolutionState);
        Assert.Null(row.ResolvedMembershipId);
    }

    [Fact]
    public async Task Database_failure_leaves_no_partial_admission_and_allows_reprocessing()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        {
            await using var revoke = new NpgsqlCommand(
                "REVOKE EXECUTE ON FUNCTION notifications.resolve_inbound_whatsapp_actor(text) FROM vinx_runtime;",
                administrator
            );
            await revoke.ExecuteNonQueryAsync();
        }

        using (var failed = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.dbfail", SenderWaId, "1948", null)
        ))
        {
            Assert.True((int)failed.StatusCode >= 500, $"expected 5xx, got {failed.StatusCode}");
        }
        Assert.Empty(await NotificationIntentSeed.ReadInboundRowsAsync(factory));

        await using (var administrator = await factory.Fixture.OpenAdministratorConnectionAsync())
        {
            await using var grant = new NpgsqlCommand(
                "GRANT EXECUTE ON FUNCTION notifications.resolve_inbound_whatsapp_actor(text) TO vinx_runtime;",
                administrator
            );
            await grant.ExecuteNonQueryAsync();
        }

        using var replay = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.dbfail", SenderWaId, "1948", null)
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("unbound", row.ResolutionState);
    }

    [Fact]
    public async Task Incompatible_replay_for_the_same_message_id_registers_a_safe_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using (var first = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.conflict", SenderWaId, "1949", null)
        ))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var replay = await PostSignedAsync(
            client,
            ImagePayload("wamid.in.conflict", "556198132902", "1950", null)
        ))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal(SenderWaId, row.WaId);
        Assert.Equal("1949", row.MediaId);
    }

    [Fact]
    public async Task Concurrent_admission_of_the_same_message_produces_a_single_row()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var payload = new
        {
            query =
                "SELECT notifications.admit_inbound_whatsapp_media("
                + "@inbound_id, @phone_number_id, @message_id, @wa_id, @message_type, @media_id, NULL, "
                + "'unbound', NULL, NULL, NULL)",
            messageId = "wamid.in.concurrent",
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(AdmitAsync)
        );
        Assert.Contains("created", results);
        Assert.Contains("replay", results);
        Assert.DoesNotContain("conflict", results);

        var row = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal(payload.messageId, row.MessageId);
        Assert.Equal("unbound", row.ResolutionState);
        return;

        async Task<string> AdmitAsync(int attempt)
        {
            await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(payload.query, administrator);
            command.Parameters.AddWithValue("inbound_id", Guid.NewGuid());
            command.Parameters.AddWithValue("phone_number_id", ChannelPhoneNumberId);
            command.Parameters.AddWithValue("message_id", payload.messageId);
            command.Parameters.AddWithValue("wa_id", SenderWaId);
            command.Parameters.AddWithValue("message_type", "image");
            command.Parameters.AddWithValue("media_id", "1959");
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task Media_admission_function_privileges_are_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_runtime', 'notifications.resolve_inbound_whatsapp_actor(text)', 'EXECUTE')"
            )
        );
        Assert.True(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_runtime', 'notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid)', 'EXECUTE')"
            )
        );
        Assert.False(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.resolve_inbound_whatsapp_actor(text)', 'EXECUTE')"
            )
        );
        Assert.False(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.admit_inbound_whatsapp_media(uuid, text, text, text, text, text, text, text, uuid, uuid, uuid)', 'EXECUTE')"
            )
        );
        await using (var dropped = new NpgsqlCommand(
            "SELECT to_regprocedure('notifications.record_inbound_whatsapp_message(uuid, text, text, text, text)') IS NULL",
            administrator
        ))
        {
            Assert.True((bool)(await dropped.ExecuteScalarAsync())!);
        }

        static async Task<bool> HasPrivilegeAsync(NpgsqlConnection connection, string expression)
        {
            await using var command = new NpgsqlCommand($"SELECT {expression}", connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }
    }

    private static async Task<HttpResponseMessage> PostSignedAsync(
        HttpClient client,
        string payload
    )
    {
        var body = Encoding.UTF8.GetBytes(payload);
        return await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new ByteArrayContent(body),
                Headers = { { "X-Hub-Signature-256", MetaWhatsAppWebhookVerifier.Compute(body, AppSecret) } },
            }
        );
    }

    private static string ImagePayload(
        string messageId,
        string waId,
        string mediaId,
        string? caption,
        bool includeChannel = true
    )
    {
        var captionField = caption is null ? string.Empty : $", \"caption\": \"{caption}\"";
        var channelField = includeChannel
            ? $"\"metadata\": {{ \"phone_number_id\": \"{ChannelPhoneNumberId}\" }},"
            : string.Empty;
        return $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    {{channelField}}
                    "messages": [
                      {
                        "id": "{{messageId}}",
                        "type": "image",
                        "from": "{{waId}}",
                        "image": { "id": "{{mediaId}}"{{captionField}} }
                      }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;
    }

    private static string ImagePayloadWithoutSender(string messageId, string mediaId)
    {
        return $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
                    "messages": [
                      {
                        "id": "{{messageId}}",
                        "type": "image",
                        "image": { "id": "{{mediaId}}" }
                      }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;
    }

    private static string DocumentPayload(
        string messageId,
        string waId,
        string mediaId,
        string? caption
    )
    {
        var captionField = caption is null ? string.Empty : $", \"caption\": \"{caption}\"";
        return $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
                    "messages": [
                      {
                        "id": "{{messageId}}",
                        "type": "document",
                        "from": "{{waId}}",
                        "document": {
                          "id": "{{mediaId}}",
                          "filename": "invoice.pdf",
                          "mime_type": "application/pdf"{{captionField}}
                        }
                      }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;
    }

    private static string AudioPayload()
    {
        return $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "123",
              "changes": [
                {
                  "value": {
                    "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" },
                    "messages": [
                      {
                        "id": "wamid.in.audio",
                        "type": "audio",
                        "from": "{{SenderWaId}}",
                        "audio": { "id": "1951" }
                      }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;
    }
}
