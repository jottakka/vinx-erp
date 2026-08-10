using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class DocumentUploadEndpointTests
{
    private const string Checksum = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] SafePayload = Encoding.UTF8.GetBytes("safe document payload");
    private static readonly byte[] EicarPayload = Encoding.ASCII.GetBytes(
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    [Fact]
    public async Task Valid_intent_creates_quarantined_metadata_before_returning_the_upload_url()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId);

        var response = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<UploadIntentResponse>();
        Assert.NotNull(result);
        Assert.Equal("quarantined", result.ProcessingState);
        Assert.NotNull(result.UploadUrl);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var file = await administrator.Files.SingleAsync(candidate => candidate.Id == result.FileId);
        Assert.Equal(scenario.CompanyId, file.CompanyId);
        Assert.Equal("quarantined", file.ProcessingState);
        Assert.Equal(request.FileName, file.OriginalFileName);
        Assert.Equal(request.Checksum, file.Checksum);
        Assert.Single(await administrator.AuditEvents.Where(audit => audit.Action == "foundation.files.upload.intent").ToListAsync());
    }

    [Theory]
    [InlineData("invoice.pdf", "image/png", 128L, Checksum, "mime_extension_mismatch")]
    [InlineData("invoice.pdf", "application/pdf", 0L, Checksum, "empty_file")]
    [InlineData("invoice.pdf", "application/pdf", 128L, "sha256:invalid", "checksum_invalid")]
    public async Task Invalid_intent_metadata_is_rejected_without_creating_a_file(
        string fileName,
        string mimeType,
        long sizeBytes,
        string checksum,
        string expectedCode)
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId) with
        {
            FileName = fileName,
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            Checksum = checksum
        };

        var response = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.Files.ToListAsync());
        var denial = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(expectedCode, denial.Action);
        Assert.Equal("{}", denial.MetadataJson);
        Assert.DoesNotContain(fileName, denial.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain(checksum, denial.MetadataJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "file_name_required")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "file_name_too_long")]
    public async Task Declaratively_invalid_file_name_is_audited_without_creating_a_file(
        string fileName,
        string expectedCode)
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId) with { FileName = fileName };

        var response = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.Files.ToListAsync());
        var denial = await administrator.AuditEvents.SingleAsync();
        Assert.Equal(AuditOutcome.Denied, denial.Outcome);
        Assert.Equal(expectedCode, denial.Action);
        Assert.Equal(expectedCode, denial.Reason);
    }

    [Fact]
    public async Task Valid_confirmation_rechecks_private_storage_and_confirms_the_file()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));

        response.EnsureSuccessStatusCode();
        Assert.Equal("confirmed", (await response.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("confirmed", (await administrator.Files.SingleAsync(candidate => candidate.Id == intent.FileId)).ProcessingState);
    }

    [Fact]
    public async Task Malware_detection_marks_the_file_failed_without_returning_a_read_url()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var malwareIntent = NewIntent(scenario.CompanyId) with
        {
            SizeBytes = EicarPayload.Length,
            Checksum = ComputeChecksum(EicarPayload)
        };
        var intentResponse = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", malwareIntent);
        intentResponse.EnsureSuccessStatusCode();
        var intent = (await intentResponse.Content.ReadFromJsonAsync<UploadIntentResponse>())!;
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, EicarPayload, file.MimeType);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));

        response.EnsureSuccessStatusCode();
        Assert.Equal("verification_failed", (await response.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var failed = await administrator.Files.SingleAsync(candidate => candidate.Id == intent.FileId);
        Assert.Equal("malware_detected", failed.FailureReason);
        Assert.Null(typeof(ConfirmationResponse).GetProperty("DownloadUrl"));
    }

    [Fact]
    public async Task Storage_read_failure_after_upload_marks_the_file_failed_without_a_download_endpoint()
    {
        var storage = new ControlledFileStorage { FailReads = true };
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));

        response.EnsureSuccessStatusCode();
        Assert.Equal("verification_failed", (await response.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/foundation/files/{intent.FileId}/download")).StatusCode);
    }

    [Fact]
    public async Task Intent_rejects_missing_membership_and_missing_upload_capability()
    {
        var storage = new ControlledFileStorage();
        await using var membershipFactory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var companyWithoutMembership = await SeedCompanyAsync(membershipFactory);

        var membershipResponse = await membershipFactory.CreateClient().PostAsJsonAsync(
            "/foundation/files/upload-intentions", NewIntent(companyWithoutMembership));

        Assert.Equal(HttpStatusCode.Forbidden, membershipResponse.StatusCode);
        Assert.Equal("membership_required", (await membershipResponse.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using (var membershipAdministrator = membershipFactory.Fixture.CreateAdministratorDbContext())
        {
            var denial = await membershipAdministrator.AuditEvents.SingleAsync();
            Assert.Equal("membership_required", denial.Action);
            Assert.Equal("{}", denial.MetadataJson);
        }

        await using var capabilityFactory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenarioWithoutUpload = await SeedScenarioAsync(capabilityFactory, grantUpload: false);

        var capabilityResponse = await capabilityFactory.CreateClient().PostAsJsonAsync(
            "/foundation/files/upload-intentions", NewIntent(scenarioWithoutUpload.CompanyId));

        Assert.Equal(HttpStatusCode.Forbidden, capabilityResponse.StatusCode);
        Assert.Equal("capability_denied", (await capabilityResponse.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var capabilityAdministrator = capabilityFactory.Fixture.CreateAdministratorDbContext();
        var capabilityDenial = await capabilityAdministrator.AuditEvents.SingleAsync();
        Assert.Equal("capability_denied", capabilityDenial.Action);
        Assert.Equal("{}", capabilityDenial.MetadataJson);
    }

    [Fact]
    public async Task Confirmation_rejects_missing_confirmation_capability()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory, grantConfirmation: false);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("capability_denied", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Confirmation_hides_a_file_from_another_authorized_tenant()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);
        var otherCompanyId = await SeedAdditionalCompanyAccessAsync(factory);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(otherCompanyId, Guid.NewGuid(), intent.FileId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("resource_not_found", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Reusing_an_intent_request_with_different_metadata_returns_idempotency_conflict()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId);

        var first = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);
        var conflict = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/upload-intentions",
            request with { Classification = "restricted" });

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await conflict.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.Files.ToListAsync());
    }

    [Fact]
    public async Task Delimiter_collision_in_intent_payload_returns_idempotency_conflict()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId) with { Classification = "financial|restricted", Origin = "user_upload" };

        var first = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);
        var collision = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/upload-intentions",
            request with { Classification = "financial", Origin = "restricted|user_upload" });

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal("idempotency_conflict", (await collision.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Repeated_intent_replays_metadata_but_generates_a_fresh_upload_url_without_a_second_audit()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId);

        var first = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);
        var replay = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        first.EnsureSuccessStatusCode();
        replay.EnsureSuccessStatusCode();
        var firstResult = (await first.Content.ReadFromJsonAsync<UploadIntentResponse>())!;
        var replayResult = (await replay.Content.ReadFromJsonAsync<UploadIntentResponse>())!;
        Assert.Equal(firstResult.FileId, replayResult.FileId);
        Assert.NotEqual(firstResult.UploadUrl, replayResult.UploadUrl);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.AuditEvents.Where(audit => audit.Action == "foundation.files.upload.intent").ToListAsync());
        Assert.Single(await administrator.IdempotencyRecords.Where(record => record.RequestId == request.RequestId).ToListAsync());
    }

    [Fact]
    public async Task Intent_replay_after_confirmation_does_not_issue_an_upload_url()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId);
        var intent = await PostIntentAsync(factory, request);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);
        var confirmation = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));
        confirmation.EnsureSuccessStatusCode();

        var replay = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("integrity_conflict", (await replay.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Intent_replay_after_verification_failure_does_not_issue_an_upload_url()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId) with { SizeBytes = EicarPayload.Length, Checksum = ComputeChecksum(EicarPayload) };
        var intent = await PostIntentAsync(factory, request);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, EicarPayload, file.MimeType);
        var confirmation = await factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));
        confirmation.EnsureSuccessStatusCode();

        var replay = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("integrity_conflict", (await replay.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Intent_replay_after_soft_delete_does_not_issue_an_upload_url()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var request = NewIntent(scenario.CompanyId);
        var intent = await PostIntentAsync(factory, request);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var file = await administrator.Files.SingleAsync(candidate => candidate.Id == intent.FileId);
            file.MarkDeleted("test", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        var replay = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);

        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal("resource_not_found", (await replay.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
    }

    [Fact]
    public async Task Confirmation_reauthorizes_after_private_inspection_finishes()
    {
        var storage = new ControlledFileStorage { BlockInspection = true };
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);

        var confirmationTask = factory.CreateClient().PostAsJsonAsync(
            "/foundation/files/confirmations",
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId));
        await storage.WaitForInspectionAsync();
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.RoleAssignments.Remove(await administrator.RoleAssignments.SingleAsync());
            await administrator.SaveChangesAsync();
        }
        storage.ReleaseInspection();

        var response = await confirmationTask;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("capability_denied", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var auditReader = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("quarantined", (await auditReader.Files.SingleAsync(candidate => candidate.Id == intent.FileId)).ProcessingState);
    }

    [Fact]
    public async Task Repeated_confirmation_replays_confirmed_state_without_a_second_audit_or_exposure()
    {
        var storage = new ControlledFileStorage();
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);
        var request = new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId);

        var first = await factory.CreateClient().PostAsJsonAsync("/foundation/files/confirmations", request);
        var replay = await factory.CreateClient().PostAsJsonAsync("/foundation/files/confirmations", request);

        first.EnsureSuccessStatusCode();
        replay.EnsureSuccessStatusCode();
        Assert.Equal("confirmed", (await first.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        Assert.Equal("confirmed", (await replay.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Single(await administrator.AuditEvents.Where(audit => audit.Action == "foundation.files.upload.confirm").ToListAsync());
        Assert.Single(await administrator.IdempotencyRecords.Where(record => record.RequestId == request.RequestId).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_confirmation_with_the_same_request_creates_one_confirmed_result()
    {
        var storage = new ControlledFileStorage
        {
            BlockInspection = true,
            InspectionsToBlock = 8
        };
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);
        var request = new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId);

        var responsesTask = Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            factory.CreateClient().PostAsJsonAsync("/foundation/files/confirmations", request)));
        await storage.WaitForInspectionAsync();
        storage.ReleaseInspection();
        var responses = await responsesTask;

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("confirmed", (await administrator.Files.SingleAsync(candidate => candidate.Id == intent.FileId)).ProcessingState);
        Assert.Single(await administrator.AuditEvents.Where(audit => audit.Action == "foundation.files.upload.confirm").ToListAsync());
        Assert.Single(await administrator.IdempotencyRecords.Where(record => record.RequestId == request.RequestId).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_confirmations_with_distinct_request_ids_reload_the_current_file_after_inspection()
    {
        var storage = new ControlledFileStorage
        {
            BlockInspection = true,
            InspectionsToBlock = 2
        };
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(fileStorage: storage);
        var scenario = await SeedScenarioAsync(factory);
        var intent = await CreateIntentAsync(factory, scenario.CompanyId);
        var file = await GetFileAsync(factory, intent.FileId);
        storage.Put(file.ProviderKey, SafePayload, file.MimeType);
        var requests = new[]
        {
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId),
            new ConfirmUploadRequest(scenario.CompanyId, Guid.NewGuid(), intent.FileId)
        };

        var responsesTask = Task.WhenAll(requests.Select(request =>
            factory.CreateClient().PostAsJsonAsync("/foundation/files/confirmations", request)));
        await storage.WaitForInspectionAsync();
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            await administrator.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE foundation.files SET checksum = {Checksum}, row_version = row_version + 1 WHERE id = {intent.FileId}");
        }

        storage.ReleaseInspection();
        var responses = await responsesTask;

        var success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal("verification_failed", (await success.Content.ReadFromJsonAsync<ConfirmationResponse>())?.ProcessingState);
        var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("integrity_conflict", (await conflict.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        await using var verification = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("verification_failed", (await verification.Files.SingleAsync(candidate => candidate.Id == intent.FileId)).ProcessingState);
        Assert.Single(await verification.IdempotencyRecords.Where(record =>
            requests.Select(request => request.RequestId).Contains(record.RequestId)).ToListAsync());
        Assert.Single(await verification.AuditEvents.Where(audit => audit.Action == "foundation.files.upload.confirm").ToListAsync());
    }

    private static UploadIntentRequest NewIntent(Guid companyId) => new(
        companyId, Guid.NewGuid(), "invoice.pdf", "application/pdf", SafePayload.Length, ComputeChecksum(SafePayload),
        "financial", "user_upload", DateTimeOffset.UtcNow.AddDays(30));

    private static string ComputeChecksum(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";

    private static async Task<UploadIntentResponse> CreateIntentAsync(FoundationApiFactory factory, Guid companyId)
    {
        return await PostIntentAsync(factory, NewIntent(companyId));
    }

    private static async Task<UploadIntentResponse> PostIntentAsync(FoundationApiFactory factory, UploadIntentRequest request)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/foundation/files/upload-intentions", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadIntentResponse>())!;
    }

    private static async Task<FileRecord> GetFileAsync(FoundationApiFactory factory, Guid fileId)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        return await administrator.Files.SingleAsync(file => file.Id == fileId);
    }

    private static async Task<Guid> SeedCompanyAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, request);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Company", factory.ActorUserId, request);
        administrator.AddRange(group, company);
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<Guid> SeedAdditionalCompanyAccessAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Other Group", factory.ActorUserId, request);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Serviços", factory.ActorUserId, request);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, request);
        var role = new Role(Guid.NewGuid(), company.Id, "Document confirmer", factory.ActorUserId, request);
        var confirmationCapability = await administrator.Capabilities.SingleAsync(capability => capability.Name == "foundation.files.confirm");
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, request);
        var grant = new RoleCapability(
            Guid.NewGuid(), company.Id, role.Id, confirmationCapability.Id, CapabilityScope.Company, factory.ActorUserId, request);
        administrator.AddRange(group, company, membership, role, assignment, grant);
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<UploadScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        bool grantUpload = true,
        bool grantConfirmation = true)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var request = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, request);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, request);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, request);
        var role = new Role(Guid.NewGuid(), company.Id, "Document operator", factory.ActorUserId, request);
        var uploadCapability = new Capability(Guid.NewGuid(), "foundation.files.upload");
        var confirmCapability = new Capability(Guid.NewGuid(), "foundation.files.confirm");
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, request);
        administrator.AddRange(group, company, membership, role, uploadCapability, confirmCapability, assignment);
        if (grantUpload)
        {
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, uploadCapability.Id, CapabilityScope.Company, factory.ActorUserId, request));
        }

        if (grantConfirmation)
        {
            administrator.RoleCapabilities.Add(new RoleCapability(
                Guid.NewGuid(), company.Id, role.Id, confirmCapability.Id, CapabilityScope.Company, factory.ActorUserId, request));
        }

        await administrator.SaveChangesAsync();
        return new UploadScenario(company.Id);
    }

    private sealed record UploadScenario(Guid CompanyId);
    private sealed record UploadIntentRequest(Guid CompanyId, Guid RequestId, string FileName, string MimeType, long SizeBytes, string Checksum, string Classification, string Origin, DateTimeOffset RetentionUntil);
    private sealed record ConfirmUploadRequest(Guid CompanyId, Guid RequestId, Guid FileId);
    private sealed record UploadIntentResponse(Guid FileId, string ProcessingState, Uri UploadUrl);
    private sealed record ConfirmationResponse(Guid FileId, string ProcessingState);
    private sealed record ErrorResponse(string Code);

    private sealed class ControlledFileStorage : FileStorage
    {
        private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource inspectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource inspectionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _uploadUrlCount;
        private int inspectionCount;
        public bool FailReads { get; init; }
        public bool BlockInspection { get; init; }
        public int InspectionsToBlock { get; init; } = 1;

        public Task EnsurePrivateContainerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SignedStorageUploadUrl> CreateSignedUploadUrlAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(new SignedStorageUploadUrl(new Uri(
                $"https://storage.test/upload/{providerKey}?attempt={Interlocked.Increment(ref _uploadUrlCount)}")));

        public Task<StorageObjectMetadata> GetObjectMetadataAsync(string providerKey, CancellationToken cancellationToken)
        {
            if (FailReads)
            {
                throw new InvalidOperationException("storage_read_failed");
            }

            var stored = _objects[providerKey];
            return Task.FromResult(new StorageObjectMetadata(
                providerKey,
                stored.Content.LongLength,
                stored.ContentType,
                ComputeChecksum(stored.Content)));
        }

        public async Task<StorageObjectInspection> InspectObjectAsync(string providerKey, CancellationToken cancellationToken)
        {
            if (FailReads)
            {
                throw new InvalidOperationException("storage_read_failed");
            }

            if (BlockInspection)
            {
                if (Interlocked.Increment(ref inspectionCount) >= InspectionsToBlock)
                {
                    inspectionStarted.TrySetResult();
                }

                await inspectionRelease.Task.WaitAsync(cancellationToken);
            }

            var stored = _objects[providerKey];
            var metadata = new StorageObjectMetadata(
                providerKey,
                stored.Content.LongLength,
                stored.ContentType,
                ComputeChecksum(stored.Content));
            return new StorageObjectInspection(
                metadata,
                stored.Content.AsSpan().IndexOf(EicarPayload) >= 0);
        }

        public Task<SignedStorageUrl> CreateSignedDownloadUrlAsync(string providerKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(string prefix, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StorageObjectSummary>>([]);
        public Task DeleteObjectAsync(string providerKey, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Put(string providerKey, byte[] content, string contentType) =>
            _objects[providerKey] = new StoredObject(content, contentType);

        public Task WaitForInspectionAsync() => inspectionStarted.Task;

        public void ReleaseInspection() => inspectionRelease.TrySetResult();

        private sealed record StoredObject(byte[] Content, string ContentType);
    }
}
