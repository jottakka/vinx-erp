using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;
using FileRecord = Vinx.Erp.Foundation.Domain.FileRecord;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD da fronteira real de leitura autorizada (#134-A / #298): RLS,
/// membership, capability files.read-ocr, liberação = confirmed COM veredito
/// durável, retenção, tamanho na política, integridade checksum/size antes
/// de abrir e stream seekable limitado. Nenhuma negação abre o storage.
/// </summary>
public sealed class AuthorizedDocumentSourceTests
{
    private static readonly byte[] Payload =
        "document content for the authorized read boundary"u8.ToArray();

    [Fact]
    public async Task Confirmed_document_with_durable_scan_opens_a_seekable_limited_stream_and_audits_allow()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");

        var document = await OpenAsync(factory, scenario, file.Id);

        Assert.True(document.Content.CanSeek);
        Assert.Equal(Payload.LongLength, document.Content.Length);
        using var reader = new StreamReader(document.Content);
        Assert.Equal("document content for the authorized read boundary", await reader.ReadToEndAsync());
        Assert.Equal(1, storage.OpenCount);
        await AssertAuditAsync(factory, file.Id, AuditOutcome.Success, null);
    }

    [Fact]
    public async Task Confirmed_row_without_durable_scan_denies_without_touching_storage()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: false, withState: "confirmed");

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));

        Assert.Equal("document_not_released", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
        await AssertAuditAsync(factory, file.Id, AuditOutcome.Denied, "document_not_released");
    }

    [Fact]
    public async Task Other_company_document_is_invisible_to_the_authorized_reader()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var foreign = await SeedForeignCompanyFileAsync(factory, storage);

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, foreign.Id));

        Assert.Equal("resource_not_found", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task Revoked_membership_denies_on_the_next_read()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");
        await OpenAsync(factory, scenario, file.Id);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var membership = await administrator.Memberships.SingleAsync(candidate =>
                candidate.Id == scenario.MembershipId);
            membership.Revoke("revoked by test", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));
        Assert.Equal("membership_required", denial.ReasonCode);
    }

    [Fact]
    public async Task Missing_read_capability_denies_even_for_an_active_member()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: false);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));

        Assert.Equal("capability_denied", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task Quarantined_deleted_and_retention_expired_documents_deny()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);

        var quarantined = await CreateFileAsync(factory, scenario, storage, Payload, withScan: false, withState: "quarantined");
        var deleted = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "deleted");
        var expired = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.Database.ExecuteSqlAsync(
                $"UPDATE foundation.files SET retention_until = clock_timestamp() - interval '1 hour' WHERE id = {expired.Id}");
        }

        Assert.Equal("document_not_released",
            (await Assert.ThrowsAsync<DocumentReadDeniedException>(() => OpenAsync(factory, scenario, quarantined.Id))).ReasonCode);
        Assert.Equal("resource_not_found",
            (await Assert.ThrowsAsync<DocumentReadDeniedException>(() => OpenAsync(factory, scenario, deleted.Id))).ReasonCode);
        Assert.Equal("retention_expired",
            (await Assert.ThrowsAsync<DocumentReadDeniedException>(() => OpenAsync(factory, scenario, expired.Id))).ReasonCode);
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task Record_size_outside_the_upload_policy_denies_before_any_allocation()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var oversized = await CreateFileAsync(
            factory, scenario, storage, Payload, withScan: true, withState: "confirmed",
            sizeBytesOverride: DocumentUploadPolicy.MaxSizeBytes + 1);

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, oversized.Id));

        Assert.Equal("document_size_out_of_policy", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task Storage_checksum_mismatch_denies_without_opening_the_object()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");
        storage.Put(file.ProviderKey, "tampered content"u8.ToArray());

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));

        Assert.Equal("checksum_mismatch", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
        await AssertAuditAsync(factory, file.Id, AuditOutcome.Denied, "checksum_mismatch");
    }

    [Fact]
    public async Task Object_larger_than_the_registered_size_is_rejected_as_divergence()
    {
        var storage = new MemoryStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");
        storage.ExtraStreamByte = true;

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));

        Assert.Equal("object_size_divergence", denial.ReasonCode);
        Assert.Equal(1, storage.OpenCount);
    }

    [Fact]
    public async Task Storage_metadata_failure_denies_fail_closed()
    {
        var storage = new MemoryStorage { FailMetadata = true };
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantReadOcr: true);
        var file = await CreateFileAsync(factory, scenario, storage, Payload, withScan: true, withState: "confirmed");

        var denial = await Assert.ThrowsAsync<DocumentReadDeniedException>(
            () => OpenAsync(factory, scenario, file.Id));

        Assert.Equal("storage_unavailable", denial.ReasonCode);
        Assert.Equal(0, storage.OpenCount);
    }

    private static async Task<AuthorizedDocument> OpenAsync(
        FoundationApiFactory factory,
        UploadScenario scenario,
        Guid fileRecordId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var source = new FoundationAuthorizedDocumentSource(
            scope.ServiceProvider.GetRequiredService<FoundationDbContext>(),
            scope.ServiceProvider.GetRequiredService<FileStorage>(),
            scope.ServiceProvider.GetRequiredService<CapabilityEvaluator>(),
            scope.ServiceProvider.GetRequiredService<AuditAppender>(),
            NullLogger<FoundationAuthorizedDocumentSource>.Instance);
        return await source.OpenReadAsync(
            new ActorContext(factory.ActorUserId, Guid.NewGuid()),
            scenario.CompanyId,
            fileRecordId,
            CancellationToken.None);
    }

    private static async Task<UploadScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        bool grantReadOcr)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, request);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, request);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, request);
        var role = new Role(Guid.NewGuid(), company.Id, "Document reader", factory.ActorUserId, request);
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, request);
        administrator.AddRange(group, company, membership, role, assignment);
        if (grantReadOcr)
        {
            var capability = await administrator.Capabilities.SingleAsync(candidate =>
                candidate.Name == KnownCapabilities.FilesReadOcr);
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, request));
        }

        await administrator.SaveChangesAsync();
        return new UploadScenario(company.Id, membership.Id);
    }

    private static async Task<FileRecord> CreateFileAsync(
        FoundationApiFactory factory,
        UploadScenario scenario,
        MemoryStorage storage,
        byte[] payload,
        bool withScan,
        string withState,
        long? sizeBytesOverride = null)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var file = new FileRecord(
            Guid.NewGuid(),
            scenario.CompanyId,
            scenario.MembershipId,
            "contrato.pdf",
            ".pdf",
            sizeBytesOverride ?? payload.LongLength,
            "application/pdf",
            Checksum(payload),
            "fiscal",
            "integration-test",
            DateTimeOffset.UtcNow.AddDays(1),
            factory.ActorUserId,
            request);
        if (withState is "confirmed" or "deleted")
        {
            file.MarkVerified(factory.ActorUserId, request);
            file.MarkConfirmed(factory.ActorUserId, request);
            if (withScan)
            {
                file.RecordScan("TestEngine", "1.0", "1.0", DateTimeOffset.UtcNow);
            }
        }

        if (withState is "deleted")
        {
            file.MarkDeleted("deleted by test", factory.ActorUserId, request);
        }

        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        storage.Put(file.ProviderKey, payload);
        return file;
    }

    private static async Task<FileRecord> SeedForeignCompanyFileAsync(
        FoundationApiFactory factory,
        MemoryStorage storage)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        // Empresa estrangeira com membership do próprio ator: a invisibilidade
        // vem do escopo de empresa da leitura (companyId da empresa autorizada),
        // não da identidade do uploader.
        var group = new Group(Guid.NewGuid(), "Foreign Group", factory.ActorUserId, request);
        var company = new Company(Guid.NewGuid(), group.Id, "Foreign Co", factory.ActorUserId, request);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, request);
        administrator.AddRange(group, company, membership);
        var file = new FileRecord(
            Guid.NewGuid(), company.Id, membership.Id, "segredo.pdf", ".pdf",
            Payload.LongLength, "application/pdf", Checksum(Payload), "fiscal",
            "integration-test", DateTimeOffset.UtcNow.AddDays(1), factory.ActorUserId, request);
        file.MarkVerified(factory.ActorUserId, request);
        file.MarkConfirmed(factory.ActorUserId, request);
        file.RecordScan("TestEngine", "1.0", "1.0", DateTimeOffset.UtcNow);
        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        storage.Put(file.ProviderKey, Payload);
        return file;
    }

    private static async Task AssertAuditAsync(
        FoundationApiFactory factory,
        Guid fileRecordId,
        AuditOutcome outcome,
        string? reason)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var audit = await administrator.AuditEvents.SingleAsync(candidate =>
            candidate.Action == FoundationAuthorizedDocumentSource.CommandName
            && candidate.ResourceId == fileRecordId
            && candidate.Outcome == outcome);
        Assert.Equal(reason, audit.Reason);
        Assert.Equal("{}", audit.MetadataJson);
    }

    private static string Checksum(byte[] payload) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}";

    private sealed record UploadScenario(Guid CompanyId, Guid MembershipId);

    /// <summary>
    /// Storage em memória contável: checksum/size derivados do conteúdo
    /// (contrato do storage privado), abertura instrumentada e falhas
    /// injetáveis para os caminhos de negação.
    /// </summary>
    private sealed class MemoryStorage : FileStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private int _openCount;

        public bool FailMetadata { get; set; }

        public bool ExtraStreamByte { get; set; }

        public int OpenCount => Volatile.Read(ref _openCount);

        public void Put(string providerKey, byte[] content) => _objects[providerKey] = content;

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
            string providerKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadObjectAsync(
            string providerKey,
            ReadOnlyMemory<byte> content,
            string contentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken)
        {
            if (FailMetadata)
            {
                throw new InvalidOperationException("metadata unavailable");
            }

            var content = _objects[providerKey];
            return Task.FromResult(new StorageObjectMetadata(
                providerKey, content.LongLength, "application/pdf", Checksum(content)));
        }

        public Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string providerKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _openCount);
            var content = _objects[providerKey];
            if (ExtraStreamByte)
            {
                var withGuard = new byte[content.Length + 1];
                content.CopyTo(withGuard, 0);
                return Task.FromResult<Stream>(new MemoryStream(withGuard, 0, withGuard.Length, false));
            }

            return Task.FromResult<Stream>(new MemoryStream(content, false));
        }

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
            string providerKey,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string prefix,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private static string Checksum(byte[] content) =>
            $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";
    }
}
