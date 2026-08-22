using System.Net;
using System.Text;
using Npgsql;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD da extração tipada (#272 PR3): documento admitido produz candidato
/// fechado (`candidate`) ou `needs_review` com motivo estável — sem defaults
/// silenciosos, sem arredondamento e sem mutação Pagaí.
/// </summary>
[Collection("notification-worker")]
public sealed class MetaWhatsAppInvoiceExtractionTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    private sealed record CandidateRow(
        string State,
        string? BeneficiaryName,
        string? Amount,
        string? Currency,
        string? DueDate,
        string? DocumentNumber,
        string? NeedsReviewReason
    );

    [Fact]
    public async Task Pdf_with_text_layer_yields_a_typed_candidate()
    {
        var extraction =
            """{"beneficiary_name":"ACME LTDA","amount":"250.00","due_date":"2026-09-05","document_number":"NF 123"}""";
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAsync(factory);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.inv.1", MetaWhatsAppDocumentInboundTests.PdfOnePage("RECIBO 250 REAIS"), "application/pdf");
        var textAi = new FakeInboundAiHandler(HttpStatusCode.OK, extraction);
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.inv.1", "media.inv.1");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: textAi,
            testFileStorage: new MetaWhatsAppDocumentInboundTests.InMemoryFileStorage(),
            testMalwareScanner: new MetaWhatsAppDocumentInboundTests.CleanScanner()
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilCandidateAsync(factory, "wamid.in.inv.1");
        Assert.Equal("candidate", row.State);
        Assert.Equal("ACME LTDA", row.BeneficiaryName);
        Assert.Equal("250.00", row.Amount);
        Assert.Equal("BRL", row.Currency);
        Assert.Equal("2026-09-05", row.DueDate);
        Assert.Equal("NF 123", row.DocumentNumber);
        Assert.Null(row.NeedsReviewReason);

        var message = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("completed", message.Status);
        Assert.Null(message.FailureReason);
        // O dado do documento viaja como dado: nada de instrução ao modelo.
        Assert.Contains("RECIBO 250 REAIS", textAi.RequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_amount_closes_as_needs_review()
    {
        await RunNeedsReviewAsync(
            "wamid.in.inv.2",
            """{"beneficiary_name":"ACME LTDA","amount":null,"due_date":"2026-09-05","document_number":null}""",
            "missing_amount"
        );
    }

    [Fact]
    public async Task Amount_outside_the_decimal_grammar_closes_as_needs_review()
    {
        await RunNeedsReviewAsync(
            "wamid.in.inv.3",
            """{"beneficiary_name":"ACME LTDA","amount":"R$ 250,00","due_date":"2026-09-05","document_number":null}""",
            "invalid_amount"
        );
    }

    [Fact]
    public async Task Missing_due_date_closes_as_needs_review()
    {
        await RunNeedsReviewAsync(
            "wamid.in.inv.4",
            """{"beneficiary_name":"ACME LTDA","amount":"250.00","due_date":null,"document_number":null}""",
            "missing_due_date"
        );
    }

    [Fact]
    public async Task Pdf_without_text_layer_closes_as_needs_review()
    {
        var extraction =
            """{"beneficiary_name":"ACME LTDA","amount":"250.00","due_date":"2026-09-05","document_number":null}""";
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAsync(factory);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.inv.5", MetaWhatsAppDocumentInboundTests.PdfOnePage(""), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.inv.5", "media.inv.5");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, extraction),
            testFileStorage: new MetaWhatsAppDocumentInboundTests.InMemoryFileStorage(),
            testMalwareScanner: new MetaWhatsAppDocumentInboundTests.CleanScanner()
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilCandidateAsync(factory, "wamid.in.inv.5");
        Assert.Equal("needs_review", row.State);
        Assert.Equal("no_text_layer", row.NeedsReviewReason);
        var message = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("completed", message.Status);
    }

    [Fact]
    public async Task Unconfigured_ai_profiles_close_extraction_as_needs_review()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAsync(factory);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.inv.6", MetaWhatsAppDocumentInboundTests.PdfOnePage("RECIBO"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.inv.6", "media.inv.6");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: new MetaWhatsAppDocumentInboundTests.InMemoryFileStorage(),
            testMalwareScanner: new MetaWhatsAppDocumentInboundTests.CleanScanner()
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilCandidateAsync(factory, "wamid.in.inv.6");
        Assert.Equal("needs_review", row.State);
        Assert.Equal("extraction_unavailable", row.NeedsReviewReason);
    }

    [Fact]
    public async Task Candidate_function_privileges_are_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text)', 'EXECUTE')"
            )
        );
        Assert.True(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.inbound_document_actor_is_allowed(uuid)', 'EXECUTE')"
            )
        );
        Assert.False(
            await HasPrivilegeAsync(
                administrator,
                "has_function_privilege('vinx_runtime', 'notifications.record_inbound_whatsapp_invoice_candidate(uuid, text, text, text, text, date, text, text)', 'EXECUTE')"
            )
        );
        Assert.False(
            await HasPrivilegeAsync(
                administrator,
                "has_table_privilege('vinx_runtime', 'notifications.inbound_whatsapp_invoice_candidates', 'SELECT')"
            )
        );

        static async Task<bool> HasPrivilegeAsync(NpgsqlConnection connection, string expression)
        {
            await using var command = new NpgsqlCommand($"SELECT {expression}", connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }
    }

    private static async Task<NotificationCompanySeed> SeedAsync(FoundationApiFactory factory)
    {
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        await MetaWhatsAppDocumentInboundTests.SeedFilesUploadCapabilityAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        return seed;
    }

    private async Task RunNeedsReviewAsync(string messageId, string extraction, string expectedReason)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await SeedAsync(factory);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia($"media.{messageId}", MetaWhatsAppDocumentInboundTests.PdfOnePage("RECIBO 250 REAIS"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, messageId, $"media.{messageId}");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            configureInboundAi: true,
            inboundTextAiHandler: new FakeInboundAiHandler(HttpStatusCode.OK, extraction),
            testFileStorage: new MetaWhatsAppDocumentInboundTests.InMemoryFileStorage(),
            testMalwareScanner: new MetaWhatsAppDocumentInboundTests.CleanScanner()
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilCandidateAsync(factory, messageId);
        Assert.Equal("needs_review", row.State);
        Assert.Equal(expectedReason, row.NeedsReviewReason);
        Assert.Null(row.Amount);
        var message = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("completed", message.Status);
        Assert.Null(message.FailureReason);
    }

    private static async Task<CandidateRow> WaitUntilCandidateAsync(
        FoundationApiFactory factory,
        string messageId
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = await ReadCandidateAsync(factory, messageId);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(250);
        }

        return (await ReadCandidateAsync(factory, messageId))!;
    }

    private static async Task<CandidateRow?> ReadCandidateAsync(
        FoundationApiFactory factory,
        string messageId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.state, c.beneficiary_name, c.amount, c.currency,
                   to_char(c.due_date, 'YYYY-MM-DD'), c.document_number, c.needs_review_reason
                FROM notifications.inbound_whatsapp_invoice_candidates AS c
                JOIN notifications.inbound_whatsapp_messages AS m ON m.id = c.inbound_message_id
                WHERE m.message_id = @message_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("message_id", messageId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CandidateRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6)
        );
    }

    private static async Task<HttpResponseMessage> PostDocumentAsync(
        HttpClient client,
        string messageId,
        string mediaId
    )
    {
        var payload = $$"""
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
                        "from": "{{SenderWaId}}",
                        "document": { "id": "{{mediaId}}", "filename": "invoice.pdf", "mime_type": "application/pdf" }
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
        var body = Encoding.UTF8.GetBytes(payload);
        return await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, WebhookPath)
            {
                Content = new ByteArrayContent(body),
                Headers = { { "X-Hub-Signature-256", MetaWhatsAppWebhookVerifier.Compute(body, AppSecret) } },
            }
        );
    }
}
