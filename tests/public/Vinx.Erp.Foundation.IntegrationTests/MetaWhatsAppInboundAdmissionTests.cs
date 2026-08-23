using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
    private const string BeforeTextImageCorrelation =
        "20260823120000_AddInboundWhatsAppReplySendState";
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Admitted_text_and_uncaptioned_image_correlate_in_either_arrival_order(bool textFirst)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(factory, seed.CompanyId, seed.RecipientUserId, $"+{SenderWaId}", true);
        using var client = factory.CreateClient();
        var text = TextPayload("wamid.in.text.pair", SenderWaId, "purchase inquiry");
        var image = ImagePayload("wamid.in.image.pair", SenderWaId, "media.pair", null);

        foreach (var payload in textFirst ? new[] { text, image } : new[] { image, text })
        {
            using var response = await PostSignedAsync(client, payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var imageId = await ReadInboundIdAsync(factory, "wamid.in.image.pair");
        Assert.Equal("purchase inquiry", await ClaimUserTextAsync(factory, imageId));
    }

    [Theory]
    [InlineData("556198132902", false)]
    [InlineData("556198132901", true)]
    public async Task Correlation_requires_the_exact_sender_and_tenant_key(string textWaId, bool differentTenant)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var first = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var second = differentTenant ? await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory) : first;
        var imageId = Guid.NewGuid();

        Assert.Equal("created", await AdmitAsync(factory, imageId, "wamid.in.image.isolated", SenderWaId, "image", first));
        Assert.Equal("created", await AdmitAsync(factory, Guid.NewGuid(), "wamid.in.text.isolated", textWaId, "text", second));
        Assert.Equal("no_match", await CorrelateAsync(factory, imageId));
        Assert.Null(await ClaimUserTextAsync(factory, imageId));
    }

    [Fact]
    public async Task Unadmitted_text_never_correlates()
    {
        await using var fixture = await StartFixtureAsync();
        var imageId = Guid.NewGuid();
        var identity = Identity.New();
        await AdmitAsync(fixture, imageId, "wamid.in.image.admitted", SenderWaId, "image", identity);
        await AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.text.unbound", SenderWaId, "text", null);

        Assert.Equal("no_match", await CorrelateAsync(fixture, imageId));
    }

    [Fact]
    public async Task Multiple_candidates_are_ambiguous_and_a_text_is_linked_once()
    {
        await using var fixture = await StartFixtureAsync();
        var identity = Identity.New();
        var imageId = Guid.NewGuid();
        await AdmitAsync(fixture, imageId, "wamid.in.image.ambiguous", SenderWaId, "image", identity);
        await AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.text.ambiguous.1", SenderWaId, "text", identity);
        await AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.text.ambiguous.2", SenderWaId, "text", identity);

        Assert.Equal("ambiguous", await CorrelateAsync(fixture, imageId));
        Assert.Equal(3, await CountAsync(fixture, "failure_reason = 'correlation_ambiguous'"));
    }

    [Fact]
    public async Task Concurrent_admission_and_correlation_create_one_link()
    {
        await using var fixture = await StartFixtureAsync();
        var identity = Identity.New();
        var imageId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        await Task.WhenAll(
            AdmitAsync(fixture, imageId, "wamid.in.image.concurrent-correlation", SenderWaId, "image", identity),
            AdmitAsync(fixture, textId, "wamid.in.text.concurrent-correlation", SenderWaId, "text", identity));

        var results = await Task.WhenAll(CorrelateAsync(fixture, imageId), CorrelateAsync(fixture, textId));
        Assert.Contains("correlated", results);
        Assert.Equal(1, await CountAsync(fixture, "correlated_text_id IS NOT NULL"));
    }

    [Fact]
    public async Task Correlated_text_is_one_to_one_and_never_expires()
    {
        await using var fixture = await StartFixtureAsync();
        var identity = Identity.New();
        var textId = Guid.NewGuid();
        var firstImageId = Guid.NewGuid();
        await AdmitAsync(fixture, textId, "wamid.in.text.one-to-one", SenderWaId, "text", identity);
        await AdmitAsync(fixture, firstImageId, "wamid.in.image.one-to-one.1", SenderWaId, "image", identity);
        await AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.image.one-to-one.2", SenderWaId, "image", identity);

        Assert.Equal("correlated", await CorrelateAsync(fixture, firstImageId));
        Assert.Equal("no_match", await CorrelateAsync(fixture, await ReadIdAsync(fixture, "wamid.in.image.one-to-one.2")));
        await ExpireDeadlineAsync(fixture, textId);
        Assert.False(await ExpireAsync(fixture, textId));
    }

    [Fact]
    public async Task Upgrade_assigns_existing_uncaptioned_images_the_exact_deadline_and_downgrade_preserves_state()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeTextImageCorrelation);
        var imageId = Guid.NewGuid();
        await AdmitAsync(fixture, imageId, "wamid.in.image.migration", SenderWaId, "image", null);
        var createdAt = await ReadTimestampAsync(fixture, imageId, "created_at");

        await migrator.Database.MigrateAsync();
        Assert.Equal(createdAt.AddSeconds(30), await ReadTimestampAsync(fixture, imageId, "correlation_deadline"));

        var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(BeforeTextImageCorrelation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, blocked.SqlState);

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await ScalarAsync(
                administrator,
                "DELETE FROM notifications.inbound_whatsapp_messages WHERE id = @id RETURNING id",
                ("id", imageId));
        }

        await migrator.Database.MigrateAsync(BeforeTextImageCorrelation);
        Assert.Equal("created", await AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.image.downgrade", SenderWaId, "image", null));
    }

    [Fact]
    public async Task Text_admission_enforces_4096_replay_conflict_and_expiration_contracts()
    {
        await using var fixture = await StartFixtureAsync();
        var identity = Identity.New();
        var textId = Guid.NewGuid();
        const string messageId = "wamid.in.text.contract";
        var body = new string('a', 4096);
        Assert.Equal("created", await AdmitAsync(fixture, textId, messageId, SenderWaId, "text", identity, body));
        var createdAt = await ReadTimestampAsync(fixture, textId, "created_at");
        Assert.Equal(createdAt.AddSeconds(30), await ReadTimestampAsync(fixture, textId, "correlation_deadline"));
        Assert.Equal("replay", await AdmitAsync(fixture, Guid.NewGuid(), messageId, SenderWaId, "text", identity, body));
        Assert.Equal("conflict", await AdmitAsync(fixture, Guid.NewGuid(), messageId, SenderWaId, "text", identity, "different"));
        await Assert.ThrowsAsync<PostgresException>(() => AdmitAsync(fixture, Guid.NewGuid(), "wamid.in.text.long", SenderWaId, "text", identity, body + "b"));
        Assert.False(await ExpireAsync(fixture, textId));
        await ExpireDeadlineAsync(fixture, textId);
        Assert.True(await ExpireAsync(fixture, textId));
        Assert.Equal("correlation_timeout", await ReadTextAsync(fixture, textId, "failure_reason"));
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
        Assert.True(await HasPrivilegeAsync(administrator, "has_function_privilege('vinx_runtime', 'notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid)', 'EXECUTE')"));
        Assert.True(await HasPrivilegeAsync(administrator, "has_function_privilege('vinx_runtime', 'notifications.correlate_inbound_whatsapp_text_image(uuid)', 'EXECUTE')"));
        Assert.False(await HasPrivilegeAsync(administrator, "has_function_privilege('public', 'notifications.admit_inbound_whatsapp_text(uuid, text, text, text, text, text, uuid, uuid, uuid)', 'EXECUTE')"));
        Assert.False(await HasPrivilegeAsync(administrator, "has_table_privilege('vinx_runtime', 'notifications.inbound_whatsapp_messages', 'UPDATE')"));
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

    private sealed record Identity(Guid UserId, Guid CompanyId, Guid MembershipId)
    {
        public static Identity New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    private static string TextPayload(string messageId, string waId, string body) => $$"""
        { "entry": [{ "changes": [{ "value": { "metadata": { "phone_number_id": "{{ChannelPhoneNumberId}}" }, "messages": [{ "id": "{{messageId}}", "type": "text", "from": "{{waId}}", "text": { "body": "{{body}}" } }] } }] }] }
        """;

    private static async Task<Guid> ReadInboundIdAsync(FoundationApiFactory factory, string messageId)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        return (Guid)(await ScalarAsync(connection, "SELECT id FROM notifications.inbound_whatsapp_messages WHERE message_id = @id", ("id", messageId)))!;
    }

    private static async Task<string?> ClaimUserTextAsync(FoundationApiFactory factory, Guid inboundId)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        return await ScalarAsync(connection, "SELECT user_text FROM notifications.claim_inbound_whatsapp_message(@id)", ("id", inboundId)) as string;
    }

    private static async Task<string> AdmitAsync(FoundationApiFactory factory, Guid inboundId, string messageId, string waId, string type, NotificationCompanySeed identity, string body = "purchase inquiry")
    {
        await using var connection = await NotificationInboxSeed.OpenRuntimeConnectionAsync(factory);
        await using var membership = await factory.Fixture.OpenAdministratorConnectionAsync();
        var membershipId = (Guid)(await ScalarAsync(membership, "SELECT id FROM foundation.memberships WHERE company_id = @company AND user_id = @user AND deleted_at IS NULL", ("company", identity.CompanyId), ("user", identity.RecipientUserId)))!;
        return await AdmitAsync(connection, inboundId, messageId, waId, type, new Identity(identity.RecipientUserId, identity.CompanyId, membershipId), body);
    }

    private static async Task<string> AdmitAsync(FoundationPostgresFixture fixture, Guid inboundId, string messageId, string waId, string type, Identity? identity, string body = "purchase inquiry")
    {
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        return await AdmitAsync(connection, inboundId, messageId, waId, type, identity, body);
    }

    private static Task<string> AdmitAsync(NpgsqlConnection connection, Guid inboundId, string messageId, string waId, string type, Identity? identity, string body)
    {
        var sql = type == "text"
            ? "SELECT notifications.admit_inbound_whatsapp_text(@inbound, @phone, @message, @wa, @body, @state, @user, @company, @membership)"
            : "SELECT notifications.admit_inbound_whatsapp_media(@inbound, @phone, @message, @wa, 'image', @media, NULL, @state, @user, @company, @membership)";
        return ScalarStringAsync(connection, sql,
            ("inbound", inboundId), ("phone", ChannelPhoneNumberId), ("message", messageId), ("wa", waId),
            ("body", body), ("media", $"media-{messageId}"), ("state", identity is null ? "unbound" : "admitted"),
            ("user", (object?)identity?.UserId ?? DBNull.Value), ("company", (object?)identity?.CompanyId ?? DBNull.Value), ("membership", (object?)identity?.MembershipId ?? DBNull.Value));
    }

    private static async Task<string> CorrelateAsync(FoundationApiFactory factory, Guid inboundId)
    {
        await using var connection = await NotificationInboxSeed.OpenRuntimeConnectionAsync(factory);
        return await CorrelateAsync(connection, inboundId);
    }

    private static async Task<string> CorrelateAsync(FoundationPostgresFixture fixture, Guid inboundId)
    {
        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        return await CorrelateAsync(connection, inboundId);
    }

    private static Task<string> CorrelateAsync(NpgsqlConnection connection, Guid inboundId) => ScalarStringAsync(connection, "SELECT notifications.correlate_inbound_whatsapp_text_image(@id)", ("id", inboundId));

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<int> CountAsync(FoundationPostgresFixture fixture, string predicate)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        return Convert.ToInt32(await ScalarAsync(connection, $"SELECT count(*) FROM notifications.inbound_whatsapp_messages WHERE {predicate}"));
    }

    private static async Task<Guid> ReadIdAsync(FoundationPostgresFixture fixture, string messageId)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        return (Guid)(await ScalarAsync(connection, "SELECT id FROM notifications.inbound_whatsapp_messages WHERE message_id = @id", ("id", messageId)))!;
    }

    private static async Task<DateTime> ReadTimestampAsync(FoundationPostgresFixture fixture, Guid id, string column)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        return (DateTime)(await ScalarAsync(connection, $"SELECT {column} FROM notifications.inbound_whatsapp_messages WHERE id = @id", ("id", id)))!;
    }

    private static async Task ExpireDeadlineAsync(FoundationPostgresFixture fixture, Guid id)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await ScalarAsync(
            connection,
            "UPDATE notifications.inbound_whatsapp_messages "
                + "SET created_at = statement_timestamp() - interval '31 seconds', "
                + "correlation_deadline = statement_timestamp() - interval '1 second' "
                + "WHERE id = @id RETURNING id",
            ("id", id));
    }

    private static async Task<bool> ExpireAsync(FoundationPostgresFixture fixture, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.WorkerConnectionString);
        await connection.OpenAsync();
        return (bool)(await ScalarAsync(connection, "SELECT notifications.expire_inbound_whatsapp_text(@id)", ("id", id)))!;
    }

    private static async Task<string?> ReadTextAsync(FoundationPostgresFixture fixture, Guid id, string column)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        return await ScalarAsync(connection, $"SELECT {column} FROM notifications.inbound_whatsapp_messages WHERE id = @id", ("id", id)) as string;
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) => (string)(await ScalarAsync(connection, sql, parameters))!;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
}
