using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.AI.DocumentContext;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Foundation.Persistence.Interceptors;
using Vinx.Erp.Paguai;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Executa os caminhos EF reais do worker (OCR autorizado e criação de
/// proposta Pagaí) na conexão do worker, provando que os grants de coluna
/// cobrem o que os serviços materializam — não apenas o ACL declarado.
/// </summary>
[Collection("notification-worker")]
public sealed class WorkerAgentEfPathsTests
{
    [Fact]
    public async Task Authorized_document_read_runs_on_the_worker_connection()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var fileId = await InboundWhatsAppAgentAdapterTests.SeedConfirmedAgentFileAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            membershipId,
            Guid.NewGuid());

        var workerDatabase = CreateWorkerDatabase(factory);
        var source = new FoundationAuthorizedDocumentSource(
            workerDatabase,
            new SingleObjectStorage($"quarantine/{seed.CompanyId:N}/{fileId:N}", "a"u8.ToArray()),
            new CapabilityEvaluator(workerDatabase),
            new AuditAppender(workerDatabase),
            NullLogger<FoundationAuthorizedDocumentSource>.Instance);

        var document = await source.OpenReadAsync(
            new ActorContext(seed.RecipientUserId, Guid.NewGuid()),
            seed.CompanyId,
            fileId,
            CancellationToken.None);

        Assert.Equal(fileId, document.FileRecordId);
        Assert.Equal(seed.CompanyId, document.CompanyId);
        using var content = new MemoryStream();
        await document.Content.CopyToAsync(content);
        Assert.Equal(1L, content.Length);
    }

    [Fact]
    public async Task Paguai_proposal_creation_runs_on_the_worker_connection()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        var membershipId = await InboundWhatsAppAgentAdapterTests.GrantAgentDocumentCapabilitiesAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId);
        var fileId = await InboundWhatsAppAgentAdapterTests.SeedConfirmedAgentFileAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            membershipId,
            Guid.NewGuid());

        var workerDatabase = CreateWorkerDatabase(factory);
        var service = new PaguaiProposalService(
            workerDatabase,
            new CapabilityEvaluator(workerDatabase),
            new AuditAppender(workerDatabase),
            NullLogger<PaguaiProposalService>.Instance);

        var result = await service.CreateAsync(
            new ActorContext(seed.RecipientUserId, Guid.NewGuid()),
            new CreatePaguaiProposalCommand(
                seed.CompanyId,
                Guid.NewGuid(),
                "Fatura do fornecedor",
                "Proposta criada pelo worker do agente",
                42.00m,
                "BRL",
                new DateOnly(2026, 9, 1),
                "Fornecedor de teste",
                "required",
                fileId,
                0.9,
                [
                    new PaguaiProposalSource("amount", $"{fileId:N}:p1"),
                    new PaguaiProposalSource("beneficiaryDisplayName", $"{fileId:N}:p2"),
                    new PaguaiProposalSource("dueDate", $"{fileId:N}:p3"),
                ],
                "test-policy-v1"),
            CancellationToken.None);

        Assert.Equal(PaguaiProposalOperationState.Created, result.State);
        Assert.NotNull(result.Proposal);
    }

    private static FoundationDbContext CreateWorkerDatabase(FoundationApiFactory factory)
    {
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(factory.Fixture.WorkerConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
            .Options;
        return new FoundationDbContext(options);
    }

    private sealed class SingleObjectStorage(string providerKey, byte[] content) : FileStorage
    {
        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(
            string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadObjectAsync(
            string key,
            ReadOnlyMemory<byte> bytes,
            string contentType,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(
            string key,
            CancellationToken cancellationToken) =>
            string.Equals(key, providerKey, StringComparison.Ordinal)
                ? Task.FromResult(new StorageObjectMetadata(
                    key,
                    content.Length,
                    "application/pdf",
                    $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}"))
                : throw new InvalidOperationException("Unexpected storage key.");

        public Task<StorageObjectInspection> InspectObjectAsync(
            string key,
            CancellationToken cancellationToken) =>
            string.Equals(key, providerKey, StringComparison.Ordinal)
                ? GetObjectMetadataAsync(key, cancellationToken).ContinueWith(
                    task => (StorageObjectInspection)new StorageObjectInspection(task.Result))
                : throw new InvalidOperationException("Unexpected storage key.");

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            string.Equals(key, providerKey, StringComparison.Ordinal)
                ? Task.FromResult<Stream>(new MemoryStream(content, writable: false))
                : throw new InvalidOperationException("Unexpected storage key.");

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(
            string key,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw new NotSupportedException();


        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
