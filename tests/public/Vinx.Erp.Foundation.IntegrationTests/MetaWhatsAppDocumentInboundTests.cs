using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Rebaseline do documento WhatsApp na materialização comum: bytes admitidos
/// só viram FileRecord confirmado após validação, scan limpo e storage privado.
/// </summary>
[Collection("notification-worker")]
public sealed class MetaWhatsAppDocumentInboundTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Admitted_image_is_materialized_as_one_confirmed_file_record()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Clean);

        var result = await scenario.MaterializeAsync(ValidPng, "image/png");

        Assert.True(result.IsConfirmed);
        Assert.Single(scenario.Storage.Uploaded);
        await using var administrator = await scenario.Factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT processing_state, classification, origin, scan_engine,
                   scan_engine_version, scan_signature_version
            FROM foundation.files
            WHERE id = @id
            """,
            administrator);
        command.Parameters.AddWithValue("id", result.ConfirmedFileId!.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("confirmed", reader.GetString(0));
        Assert.Equal("agentic_attachment", reader.GetString(1));
        Assert.Equal("agent_channel", reader.GetString(2));
        Assert.Equal("TestScanner", reader.GetString(3));
        Assert.Equal("1", reader.GetString(4));
        Assert.Equal("test-signature", reader.GetString(5));
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    public async Task Disallowed_or_malformed_media_is_rejected_before_scan_and_storage(
        string mimeType)
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Clean);

        var result = await scenario.MaterializeAsync([1, 2, 3], mimeType);

        Assert.Equal("document_rejected", result.FailureReason);
        Assert.Equal(0, scenario.Scanner.Calls);
        Assert.Empty(scenario.Storage.Uploaded);
    }

    [Fact]
    public async Task Malware_detected_fails_closed_without_upload_or_file_record()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.MalwareDetected);

        var result = await scenario.MaterializeAsync(ValidPng, "image/png");

        Assert.Equal("document_rejected", result.FailureReason);
        Assert.Empty(scenario.Storage.Uploaded);
        Assert.Equal(0, await scenario.CountFilesAsync());
    }

    [Fact]
    public async Task Scanner_unavailable_fails_closed_without_upload_or_file_record()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Unavailable);

        var result = await scenario.MaterializeAsync(ValidPng, "image/png");

        Assert.Equal("document_scan_unavailable", result.FailureReason);
        Assert.Empty(scenario.Storage.Uploaded);
        Assert.Equal(0, await scenario.CountFilesAsync());
    }

    [Fact]
    public async Task Revoked_capability_prevents_scan_storage_and_file_creation()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Clean);
        await scenario.RevokeAssignmentsAsync();

        var result = await scenario.MaterializeAsync(ValidPng, "image/png");

        Assert.Equal("capability_missing", result.FailureReason);
        Assert.Equal(0, scenario.Scanner.Calls);
        Assert.Empty(scenario.Storage.Uploaded);
        Assert.Equal(0, await scenario.CountFilesAsync());
    }

    [Fact]
    public async Task Accepted_upload_with_lost_ack_reuses_one_storage_key_and_file_record()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Clean);
        var inboundId = Guid.NewGuid();

        var first = await scenario.MaterializeAsync(ValidPng, "image/png", inboundId);
        var replay = await scenario.MaterializeAsync(ValidPng, "image/png", inboundId);

        Assert.Equal(inboundId, first.ConfirmedFileId);
        Assert.Equal(first.ConfirmedFileId, replay.ConfirmedFileId);
        Assert.Single(scenario.Storage.Uploaded);
        Assert.Equal(1, scenario.Scanner.Calls);
        Assert.Equal(1, await scenario.CountFilesAsync());
    }

    [Fact]
    public async Task Commit_ack_loss_never_deletes_a_committed_file_object()
    {
        await using var scenario = await Scenario.CreateAsync(MalwareScanStatus.Clean);
        var inboundId = Guid.NewGuid();
        CommitAcknowledgementFailure.Arm(scenario.Factory.Fixture);

        var result = await scenario.MaterializeAsync(ValidPng, "image/png", inboundId);

        Assert.Equal(inboundId, result.ConfirmedFileId);
        Assert.Single(scenario.Storage.Uploaded);
        Assert.Equal(0, scenario.Storage.DeleteCalls);
        Assert.Equal(1, await scenario.CountFilesAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(
            FoundationApiFactory factory,
            FoundationDbContext database,
            NotificationCompanySeed seed,
            Guid membershipId,
            ControlledStorage storage,
            ControlledScanner scanner)
        {
            Factory = factory;
            Database = database;
            Seed = seed;
            MembershipId = membershipId;
            Storage = storage;
            Scanner = scanner;
        }

        public FoundationApiFactory Factory { get; }
        public FoundationDbContext Database { get; }
        public NotificationCompanySeed Seed { get; }
        public Guid MembershipId { get; }
        public ControlledStorage Storage { get; }
        public ControlledScanner Scanner { get; }

        public static async Task<Scenario> CreateAsync(MalwareScanStatus status)
        {
            var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
            var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
            var membershipId =
                await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
                    factory,
                    seed.CompanyId,
                    seed.RecipientUserId);
            var options = new DbContextOptionsBuilder<FoundationDbContext>()
                .UseNpgsql(factory.Fixture.WorkerConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(
                    new RowVersionInterceptor(),
                    new CommitAcknowledgementFailureInterceptor())
                .Options;
            return new Scenario(
                factory,
                new FoundationDbContext(options),
                seed,
                membershipId,
                new ControlledStorage(),
                new ControlledScanner(status));
        }

        public Task<InboundAttachmentMaterializationResult> MaterializeAsync(
            byte[] bytes,
            string mimeType,
            Guid? inboundMessageId = null)
        {
            var materializer = new InboundAttachmentMaterializer(
                Database,
                Storage,
                Scanner,
                Options.Create(new MalwareScannerOptions { Timeout = TimeSpan.FromSeconds(5) }),
                new CapabilityEvaluator(Database));
            return materializer.MaterializeAsync(
                new InboundAttachmentMaterializationRequest(
                    inboundMessageId ?? Guid.NewGuid(),
                    new ActorContext(Seed.RecipientUserId, Guid.NewGuid()),
                    Seed.CompanyId,
                    MembershipId,
                    mimeType,
                    bytes),
                CancellationToken.None);
        }

        public async Task RevokeAssignmentsAsync()
        {
            await using var administrator = await Factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE foundation.role_assignments SET deleted_at = clock_timestamp(), deleted_by_user_id = @actor, deleted_reason = 'test_revocation' WHERE company_id = @company_id AND membership_id = @membership_id",
                administrator);
            command.Parameters.AddWithValue("actor", Factory.ActorUserId);
            command.Parameters.AddWithValue("company_id", Seed.CompanyId);
            command.Parameters.AddWithValue("membership_id", MembershipId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> CountFilesAsync()
        {
            await using var administrator = await Factory.Fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM foundation.files WHERE company_id = @company_id",
                administrator);
            command.Parameters.AddWithValue("company_id", Seed.CompanyId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    private sealed class ControlledScanner(MalwareScanStatus status) : IMalwareScanner
    {
        public int Calls { get; private set; }

        public async Task<MalwareScanVerdict> ScanAsync(
            MalwareScanRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            await using var stream = await request.OpenContentAsync!(cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            var checksum = $"sha256:{Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant()}";
            return new MalwareScanVerdict(
                status,
                status == MalwareScanStatus.Clean ? "TestScanner" : null,
                status == MalwareScanStatus.Clean ? "1" : null,
                status == MalwareScanStatus.Clean ? "test-signature" : null,
                DateTimeOffset.UtcNow,
                checksum,
                status == MalwareScanStatus.Unavailable ? "scanner_not_configured" : null);
        }
    }

    private sealed class ControlledStorage : FileStorage
    {
        private int deleteCalls;

        public Dictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);

        public int DeleteCalls => Volatile.Read(ref deleteCalls);

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UploadObjectAsync(
            string providerKey,
            ReadOnlyMemory<byte> content,
            string contentType,
            CancellationToken cancellationToken)
        {
            Uploaded.Add(providerKey, content.ToArray());
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref deleteCalls);
            Uploaded.Remove(providerKey);
            return Task.CompletedTask;
        }

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(string providerKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
