using System.Net;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD do documento seguro (#272 PR2): documento admitido é baixado,
/// validado (MIME/bytes/páginas), escaneado e armazenado em quarentena
/// privada com registro durável — sem OCR, sem Pagaí e sem resposta.
/// </summary>
[Collection("notification-worker")]
public sealed class MetaWhatsAppDocumentInboundTests
{
    private const string WebhookPath = "/foundation/notifications/whatsapp/webhook";
    // Segredo de teste configurado no factory; nenhum valor real.
    private const string AppSecret = "test-meta-app-secret";
    private const string ChannelPhoneNumberId = "111111111111111";
    private const string SenderWaId = "556198132901";

    private sealed record InboundDocumentRow(
        Guid InboundMessageId,
        Guid CompanyId,
        Guid UserId,
        string MimeType,
        long SizeBytes,
        string Checksum,
        int? PageCount,
        string ScanStatus,
        string ProviderKey,
        string State
    );

    [Fact]
    public async Task Admitted_pdf_is_quarantined_with_checksum_pages_and_clean_scan()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var pdf = MinimalPdf.OnePage("RECIBO 250 REAIS");
        var storage = new InMemoryFileStorage();
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.q1", pdf, "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.q1", "media.doc.q1");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: storage,
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilDocumentRowAsync(factory, "wamid.in.doc.q1");
        Assert.Equal(seed.CompanyId, row.CompanyId);
        Assert.Equal(seed.RecipientUserId, row.UserId);
        Assert.Equal("application/pdf", row.MimeType);
        Assert.Equal(pdf.LongLength, row.SizeBytes);
        Assert.Equal(ChecksumOf(pdf), row.Checksum);
        Assert.Equal(1, row.PageCount);
        Assert.Equal("clean", row.ScanStatus);
        Assert.Equal("admitted", row.State);
        Assert.StartsWith("quarantine/whatsapp/", row.ProviderKey, StringComparison.Ordinal);

        var uploaded = Assert.Single(storage.Uploaded);
        Assert.Equal(row.ProviderKey, uploaded.Key);
        Assert.Equal("application/pdf", uploaded.ContentType);
        Assert.Equal(pdf, uploaded.Content);

        var message = Assert.Single(await NotificationIntentSeed.ReadInboundRowsAsync(factory));
        Assert.Equal("completed", message.Status);
        Assert.Null(message.FailureReason);
    }

    [Fact]
    public async Task Disallowed_media_type_is_rejected_without_quarantine()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var storage = new InMemoryFileStorage();
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.txt", "not-a-pdf"u8.ToArray(), "text/plain");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.txt", "media.doc.txt");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: storage,
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.txt",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("document_rejected", message.FailureReason);
        Assert.Empty(storage.Uploaded);
        await AssertDocumentRowAbsentAsync(factory, "wamid.in.doc.txt");
    }

    [Fact]
    public async Task Malformed_pdf_is_rejected_without_quarantine()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.bad", "definitely not a pdf"u8.ToArray(), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.bad", "media.doc.bad");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: new InMemoryFileStorage(),
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.bad",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("document_rejected", message.FailureReason);
    }

    [Fact]
    public async Task Malware_detected_fails_closed_without_upload()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var storage = new InMemoryFileStorage();
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.mal", MinimalPdf.OnePage("x"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.mal", "media.doc.mal");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: storage,
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.MalwareDetected)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.mal",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("document_rejected", message.FailureReason);
        Assert.Empty(storage.Uploaded);
    }

    [Fact]
    public async Task Unavailable_scanner_fails_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.scan", MinimalPdf.OnePage("x"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.scan", "media.doc.scan");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: new InMemoryFileStorage(),
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Unavailable)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.scan",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("document_scan_unavailable", message.FailureReason);
    }

    [Fact]
    public async Task Unconfigured_storage_fails_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.storage", MinimalPdf.OnePage("x"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.storage", "media.doc.storage");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.storage",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("document_storage_unavailable", message.FailureReason);
    }

    [Fact]
    public async Task Missing_capability_fails_closed_without_document()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.cap", MinimalPdf.OnePage("x"), "application/pdf");
        using var client = factory.CreateClient();

        using var posted = await PostDocumentAsync(client, "wamid.in.doc.cap", "media.doc.cap");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: new InMemoryFileStorage(),
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var message = await NotificationIntentSeed.WaitUntilInboundAsync(
            factory,
            "wamid.in.doc.cap",
            "failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("capability_missing", message.FailureReason);
        await AssertDocumentRowAbsentAsync(factory, "wamid.in.doc.cap");
    }

    [Fact]
    public async Task Webhook_replay_processes_the_document_only_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await SeedAdmittedSenderAsync(factory);
        await SeedFilesUploadCapabilityAsync(factory, seed.CompanyId, seed.RecipientUserId);
        var storage = new InMemoryFileStorage();
        var meta = new FakeInboundMetaHandler();
        meta.AddMedia("media.doc.once", MinimalPdf.OnePage("x"), "application/pdf");
        using var client = factory.CreateClient();

        var payload = DocumentPayload("wamid.in.doc.once", "media.doc.once");
        using (var first = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var replay = await PostSignedAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: meta,
            testFileStorage: storage,
            testMalwareScanner: new ControlledVerdictScanner(MalwareScanStatus.Clean)
        );
        using var _ = worker.CreateClient();

        var row = await WaitUntilDocumentRowAsync(factory, "wamid.in.doc.once");
        Assert.Equal("admitted", row.State);
        Assert.Single(storage.Uploaded);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(storage.Uploaded);
    }

    [Fact]
    public async Task Document_function_privileges_are_closed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await ScalarAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.claim_inbound_whatsapp_document(uuid)', 'EXECUTE')"
            )
        );
        Assert.True(
            await ScalarAsync(
                administrator,
                "has_function_privilege('vinx_worker', 'notifications.complete_inbound_whatsapp_document(uuid, text, bigint, text, integer, text, text, text, timestamp with time zone, text, uuid)', 'EXECUTE')"
            )
        );
        Assert.False(
            await ScalarAsync(
                administrator,
                "has_function_privilege('vinx_runtime', 'notifications.claim_inbound_whatsapp_document(uuid)', 'EXECUTE')"
            )
        );
        Assert.False(
            await ScalarAsync(
                administrator,
                "has_table_privilege('vinx_runtime', 'notifications.inbound_whatsapp_documents', 'SELECT')"
            )
        );
        Assert.False(
            await ScalarAsync(
                administrator,
                "has_table_privilege('vinx_worker', 'notifications.inbound_whatsapp_documents', 'SELECT')"
            )
        );

        static async Task<bool> ScalarAsync(NpgsqlConnection connection, string expression)
        {
            await using var command = new NpgsqlCommand($"SELECT {expression}", connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }
    }

    private static async Task<NotificationCompanySeed> SeedAdmittedSenderAsync(
        FoundationApiFactory factory
    )
    {
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            $"+{SenderWaId}",
            isValidated: true
        );
        return seed;
    }

    internal static async Task SeedFilesUploadCapabilityAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid userId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var lookup = new NpgsqlCommand(
            "SELECT id FROM foundation.memberships WHERE company_id = @company_id AND user_id = @user_id AND deleted_at IS NULL",
            administrator
        );
        lookup.Parameters.AddWithValue("company_id", companyId);
        lookup.Parameters.AddWithValue("user_id", userId);
        var membershipId = (Guid)(await lookup.ExecuteScalarAsync())!;

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@role_id, @company_id, 'Documentos', @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @role_id, @capability_id, 'company', @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.role_assignments (id, company_id, role_id, membership_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (gen_random_uuid(), @company_id, @role_id, @membership_id, @actor, @actor, @request_id, clock_timestamp(), clock_timestamp());
            """,
            administrator
        );
        command.Parameters.AddWithValue("role_id", Guid.NewGuid());
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("actor", factory.ActorUserId);
        command.Parameters.AddWithValue("request_id", Guid.NewGuid());
        command.Parameters.AddWithValue("capability_id", KnownCapabilities.FilesUploadId);
        command.Parameters.AddWithValue("membership_id", membershipId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<InboundDocumentRow> WaitUntilDocumentRowAsync(
        FoundationApiFactory factory,
        string messageId
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = await ReadDocumentRowAsync(factory, messageId);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(250);
        }

        return (await ReadDocumentRowAsync(factory, messageId))!;
    }

    private static async Task<InboundDocumentRow?> ReadDocumentRowAsync(
        FoundationApiFactory factory,
        string messageId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT d.inbound_message_id, d.company_id, d.user_id, d.mime_type, d.size_bytes,
                   d.checksum, d.page_count, d.scan_status, d.provider_key, d.state
                FROM notifications.inbound_whatsapp_documents AS d
                JOIN notifications.inbound_whatsapp_messages AS m ON m.id = d.inbound_message_id
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

        return new InboundDocumentRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9)
        );
    }

    private static async Task AssertDocumentRowAbsentAsync(
        FoundationApiFactory factory,
        string messageId
    )
    {
        Assert.Null(await ReadDocumentRowAsync(factory, messageId));
    }

    private static async Task<HttpResponseMessage> PostDocumentAsync(
        HttpClient client,
        string messageId,
        string mediaId
    )
    {
        var payload = DocumentPayload(messageId, mediaId);
        return await PostSignedAsync(client, payload);
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

    private static string DocumentPayload(string messageId, string mediaId)
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
                        "type": "document",
                        "from": "{{SenderWaId}}",
                        "document": {
                          "id": "{{mediaId}}",
                          "filename": "invoice.pdf",
                          "mime_type": "application/pdf",
                          "caption": "recibo do mes"
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

    private static string ChecksumOf(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    /// <summary>PDF mínimo válido com camada de texto (parseável pelo PdfPig).</summary>
    internal static byte[] PdfOnePage(string text) => MinimalPdf.OnePage(text);

    private static class MinimalPdf
    {
        public static byte[] OnePage(string text)
        {
            var content = Encoding.ASCII.GetBytes(
                $"BT /F1 12 Tf 20 100 Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET"
            );
            var objects = new[]
            {
                "<</Type/Catalog/Pages 2 0 R>>",
                "<</Type/Pages/Kids[3 0 R]/Count 1>>",
                "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
                $"<</Length {content.Length}>>stream\n",
                "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            };
            var buffer = new MemoryStream();
            buffer.Write("%PDF-1.4\n"u8);
            var offsets = new long[objects.Length];
            for (var index = 0; index < objects.Length; index++)
            {
                offsets[index] = buffer.Position;
                buffer.Write(Encoding.ASCII.GetBytes($"{index + 1} 0 obj\n"));
                buffer.Write(Encoding.ASCII.GetBytes(objects[index]));
                if (index == 3)
                {
                    buffer.Write(content);
                    buffer.Write("\nendstream\n"u8);
                }

                buffer.Write("\nendobj\n"u8);
            }

            var xrefOffset = buffer.Position;
            buffer.Write(Encoding.ASCII.GetBytes($"xref\n0 {objects.Length + 1}\n"));
            buffer.Write("0000000000 65535 f \n"u8);
            foreach (var offset in offsets)
            {
                buffer.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
            }

            buffer.Write(
                Encoding.ASCII.GetBytes(
                    $"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xrefOffset}\n%%EOF"
                )
            );
            return buffer.ToArray();
        }
    }

    internal sealed record UploadedObject(string Key, byte[] Content, string ContentType);

    internal sealed class InMemoryFileStorage : FileStorage
    {
        private readonly Lock _gate = new();
        private readonly List<UploadedObject> _uploaded = [];

        public IReadOnlyList<UploadedObject> Uploaded
        {
            get
            {
                lock (_gate)
                {
                    return _uploaded.ToList();
                }
            }
        }

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
            string providerKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task UploadObjectAsync(
            string providerKey,
            ReadOnlyMemory<byte> content,
            string contentType,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                _uploaded.Add(new UploadedObject(providerKey, content.ToArray(), contentType));
            }

            return Task.CompletedTask;
        }

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(
            string providerKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<StorageObjectInspection> InspectObjectAsync(
            string providerKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
            string providerKey,
            TimeSpan lifetime,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string prefix,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal class ControlledVerdictScanner(MalwareScanStatus status) : IMalwareScanner
    {
        public Task<MalwareScanVerdict> ScanAsync(
            MalwareScanRequest request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new MalwareScanVerdict(
                    status,
                    "test-engine",
                    "1.0",
                    "2.0",
                    DateTimeOffset.UtcNow,
                    null,
                    null
                )
            );
    }

    internal sealed class CleanScanner() : ControlledVerdictScanner(MalwareScanStatus.Clean);
}
