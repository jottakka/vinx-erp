using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Paguai;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class PaguaiProposalEndpointTests
{
    [Fact]
    public async Task Completed_ledger_replay_materializes_and_returns_the_same_proposal_id()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var command = await SeedCompletedPaguaiLedgerAsync(factory, scenario);

        var first = await ExecuteAgentAsync(factory, command);
        var replay = await ExecuteAgentAsync(factory, command);

        Assert.Equal(AgentExecutionDisposition.Completed, first.Disposition);
        Assert.NotNull(first.PaguaiProposalId);
        Assert.Equal(first.PaguaiProposalId, replay.PaguaiProposalId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.PaguaiDraftProposals.CountAsync(proposal =>
            proposal.CompanyId == scenario.CompanyId
            && proposal.ActorUserId == factory.ActorUserId
            && proposal.RequestId == command.RequestId));
        Assert.Equal(
            AgentPolicyVersion.Current,
            (await administrator.PaguaiDraftProposals.SingleAsync(proposal =>
                proposal.RequestId == command.RequestId)).PolicyVersion);
    }

    [Fact]
    public async Task Proposal_materialization_failure_retries_from_completed_ledger_only()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var command = await SeedCompletedPaguaiLedgerAsync(factory, scenario);
        await RevokeAuditAppendAsync(factory);

        var unavailable = await ExecuteAgentAsync(factory, command);

        Assert.Equal(AgentExecutionDisposition.FailedClosed, unavailable.Disposition);
        Assert.Null(unavailable.PaguaiProposalId);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            Assert.Equal(AgentExecutionLedgerState.Completed,
                (await administrator.AgentExecutionLedger.SingleAsync()).State);
            Assert.Empty(await administrator.PaguaiDraftProposals.ToListAsync());
        }

        await GrantAuditAppendAsync(factory);
        var recovered = await ExecuteAgentAsync(factory, command);

        Assert.Equal(AgentExecutionDisposition.Completed, recovered.Disposition);
        Assert.NotNull(recovered.PaguaiProposalId);
    }

    [Fact]
    public async Task Concurrent_completed_ledger_replays_converge_on_one_proposal_id()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var command = await SeedCompletedPaguaiLedgerAsync(factory, scenario);

        var results = await Task.WhenAll(
            ExecuteAgentAsync(factory, command),
            ExecuteAgentAsync(factory, command));

        Assert.All(results, result => Assert.Equal(AgentExecutionDisposition.Completed, result.Disposition));
        Assert.NotNull(results[0].PaguaiProposalId);
        Assert.Equal(results[0].PaguaiProposalId, results[1].PaguaiProposalId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.PaguaiDraftProposals.CountAsync(proposal =>
            proposal.RequestId == command.RequestId));
    }

    [Theory]
    [InlineData("consumed")]
    [InlineData("rejected")]
    [InlineData("invalidated")]
    [InlineData("expired")]
    public async Task Terminal_proposal_ledger_replay_never_creates_a_new_pending_proposal(string terminalState)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var command = await SeedCompletedPaguaiLedgerAsync(factory, scenario);
        var first = await ExecuteAgentAsync(factory, command);
        var proposalId = Assert.IsType<Guid>(first.PaguaiProposalId);
        await SetProposalTerminalAsync(factory, proposalId, terminalState);

        var replay = await ExecuteAgentAsync(factory, command);

        Assert.Equal(AgentExecutionDisposition.Completed, replay.Disposition);
        Assert.Equal(proposalId, replay.PaguaiProposalId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.PaguaiDraftProposals.CountAsync(proposal =>
            proposal.RequestId == command.RequestId));
        Assert.False(await administrator.PaguaiDraftProposals.AnyAsync(proposal =>
            proposal.RequestId == command.RequestId
            && proposal.Id != proposalId
            && proposal.State == PaguaiDraftProposal.PendingState));
    }

    [Fact]
    public async Task Safe_fields_are_persisted_and_get_returns_only_the_typed_proposal()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proposal = await CreateProposalAsync(factory, scenario);

        using var response = await factory.CreateClient().GetAsync(
            $"/paguai/draft-proposals/{proposal.Id}?companyId={scenario.CompanyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["amount", "beneficiaryDisplayName", "companyId", "confidence", "createdAt", "currency", "description",
                "dueDate", "expiresAt", "id", "invoiceFileId", "proofRequirement", "sourceReferences",
                "state", "title"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("250.00", body.RootElement.GetProperty("amount").GetString());
        Assert.Equal(proposal.Id, body.RootElement.GetProperty("id").GetGuid());
        Assert.DoesNotContain("raw", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ocr", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirmation_promotes_the_file_consumes_the_proposal_and_replays_the_same_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proposal = await CreateProposalAsync(factory, scenario);
        var request = new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() };
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync($"/paguai/draft-proposals/{proposal.Id}/confirm", request);
        using var replay = await client.PostAsJsonAsync($"/paguai/draft-proposals/{proposal.Id}/confirm", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstBody.RootElement.GetProperty("id").GetGuid(), replayBody.RootElement.GetProperty("id").GetGuid());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("paguai_request_invoice", (await administrator.Files.SingleAsync(file => file.Id == scenario.FileId)).Classification);
        var stored = await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == proposal.Id);
        Assert.Equal(PaguaiDraftProposal.ConsumedState, stored.State);
        Assert.Equal(1, await administrator.PaguaiRequests.CountAsync(draft => draft.SourceProposalId == proposal.Id));
        Assert.Equal(1, await administrator.AuditEvents.CountAsync(audit =>
            audit.Action == "paguai.proposal.confirm" && audit.ResourceId == proposal.Id));
    }

    [Fact]
    public async Task Same_request_with_another_proposal_returns_payload_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var firstProposal = await CreateProposalAsync(factory, scenario);
        var secondFile = await SeedAgenticFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "second");
        var secondProposal = await CreateProposalAsync(factory, scenario with { FileId = secondFile.Id }, Guid.NewGuid());
        var request = new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() };
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync($"/paguai/draft-proposals/{firstProposal.Id}/confirm", request);
        using var conflict = await client.PostAsJsonAsync($"/paguai/draft-proposals/{secondProposal.Id}/confirm", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("{\"code\":\"idempotency_conflict\"}", await conflict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rejected_and_database_clock_expired_proposals_are_terminal_and_unavailable()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var rejected = await CreateProposalAsync(factory, scenario);
        using var client = factory.CreateClient();
        var rejection = new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() };

        using var reject = await client.PostAsJsonAsync($"/paguai/draft-proposals/{rejected.Id}/reject", rejection);
        using var rejectedConfirm = await client.PostAsJsonAsync(
            $"/paguai/draft-proposals/{rejected.Id}/confirm",
            new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejectedConfirm.StatusCode);
        Assert.Equal("{\"code\":\"proposal_unavailable\"}", await rejectedConfirm.Content.ReadAsStringAsync());

        var secondFile = await SeedAgenticFileAsync(factory, scenario.CompanyId, scenario.MembershipId, "expired");
        var expired = await CreateProposalAsync(factory, scenario with { FileId = secondFile.Id }, Guid.NewGuid());
        await SetExpiredByDatabaseClockAsync(factory, expired.Id);
        using var expiredConfirm = await client.PostAsJsonAsync(
            $"/paguai/draft-proposals/{expired.Id}/confirm",
            new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, expiredConfirm.StatusCode);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(PaguaiDraftProposal.InvalidatedState,
            (await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == expired.Id)).State);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("capability")]
    [InlineData("file")]
    public async Task Revoked_confirmation_dependency_invalidates_without_creating_a_draft(string dependency)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proposal = await CreateProposalAsync(factory, scenario);
        await RevokeDependencyAsync(factory, scenario, dependency);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            $"/paguai/draft-proposals/{proposal.Id}/confirm",
            new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"code\":\"proposal_unavailable\"}", await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(await administrator.PaguaiRequests.AnyAsync(draft => draft.SourceProposalId == proposal.Id));
        Assert.Equal(PaguaiDraftProposal.InvalidatedState,
            (await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == proposal.Id)).State);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_classification_draft_proposal_and_audit()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proposal = await CreateProposalAsync(factory, scenario);
        await RevokeAuditAppendAsync(factory);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            $"/paguai/draft-proposals/{proposal.Id}/confirm",
            new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal("agentic_attachment", (await administrator.Files.SingleAsync(file => file.Id == scenario.FileId)).Classification);
        Assert.False(await administrator.PaguaiRequests.AnyAsync(draft => draft.SourceProposalId == proposal.Id));
        Assert.Equal(PaguaiDraftProposal.PendingState,
            (await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == proposal.Id)).State);
        Assert.False(await administrator.AuditEvents.AnyAsync(audit =>
            audit.ResourceId == proposal.Id && audit.Action == "paguai.proposal.confirm"));
    }

    [Fact]
    public async Task Concurrent_confirmations_create_one_draft_and_consume_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        var proposal = await CreateProposalAsync(factory, scenario);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync($"/paguai/draft-proposals/{proposal.Id}/confirm",
                new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() }),
            secondClient.PostAsJsonAsync($"/paguai/draft-proposals/{proposal.Id}/confirm",
                new { companyId = scenario.CompanyId, requestId = Guid.NewGuid() }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.PaguaiRequests.CountAsync(draft => draft.SourceProposalId == proposal.Id));
        Assert.Equal(PaguaiDraftProposal.ConsumedState,
            (await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == proposal.Id)).State);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Proposal_mutations_are_declared_with_antiforgery()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var suffix in new[] { "confirm", "reject" })
        {
            var operation = document.RootElement.GetProperty("paths")
                .GetProperty($"/paguai/draft-proposals/{{id}}/{suffix}").GetProperty("post");
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray()
                .Select(parameter => ResolveParameter(document.RootElement, parameter)), parameter =>
                parameter.TryGetProperty("name", out var name)
                && name.GetString() == "X-CSRF-TOKEN"
                && parameter.GetProperty("in").GetString() == "header"
                && (!parameter.TryGetProperty("required", out var required)
                    || !required.GetBoolean()));
        }
    }

    private static JsonElement ResolveParameter(JsonElement document, JsonElement parameter)
    {
        var resolved = parameter;
        for (var depth = 0; depth < 8; depth++)
        {
            if (!resolved.TryGetProperty("$ref", out var reference))
            {
                return resolved;
            }

            const string prefix = "#/components/parameters/";
            var referenceValue = reference.GetString()
                ?? throw new InvalidOperationException("The OpenAPI parameter reference was empty.");
            Assert.StartsWith(prefix, referenceValue, StringComparison.Ordinal);
            resolved = document.GetProperty("components").GetProperty("parameters")
                .GetProperty(referenceValue[prefix.Length..]);
        }

        throw new InvalidOperationException("The OpenAPI parameter reference chain was recursive.");
    }

    private static async Task<PaguaiProposal> CreateProposalAsync(
        FoundationApiFactory factory,
        Scenario scenario,
        Guid? requestId = null)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PaguaiProposalService>();
        var result = await service.CreateAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            NewProposalCommand(scenario, requestId ?? Guid.NewGuid()),
            CancellationToken.None);
        Assert.Equal(PaguaiProposalOperationState.Created, result.State);
        return Assert.IsType<PaguaiProposal>(result.Proposal);
    }

    private static CreatePaguaiProposalCommand NewProposalCommand(Scenario scenario, Guid requestId) => new(
        scenario.CompanyId,
        requestId,
        "Office supplies",
        "Replacement paper",
        250.00m,
        "BRL",
        new DateOnly(2026, 9, 10),
        "Fornecedor Demo",
        "optional",
        scenario.FileId,
        0.91,
        [
            new PaguaiProposalSource("amount", $"{scenario.FileId:N}:p1"),
            new PaguaiProposalSource("dueDate", $"{scenario.FileId:N}:p1"),
            new PaguaiProposalSource("beneficiaryDisplayName", $"{scenario.FileId:N}:p1"),
        ],
        AgentPolicyVersion.Current);

    private static async Task<AgentCommand> SeedCompletedPaguaiLedgerAsync(
        FoundationApiFactory factory,
        Scenario scenario)
    {
        var requestId = Guid.NewGuid();
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);
        var command = new AgentCommand(
            actor,
            "web",
            InboundMessageId: null,
            requestId,
            AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
            "extraia a fatura",
            scenario.CompanyId,
            [scenario.FileId]);
        var proposalCommand = NewProposalCommand(scenario, requestId);
        var safeOutcome = new AgentOutcome(
            AgentOutcomeStatus.Completed,
            AgentOutputGate.Ok,
            Text: null,
            ProposedActionName: null,
            actor.CorrelationId,
            TimeSpan.FromMilliseconds(10),
            new PaguaiDocumentProposal(
                proposalCommand.Title,
                proposalCommand.Description,
                proposalCommand.Amount,
                proposalCommand.Currency,
                proposalCommand.DueDate,
                proposalCommand.BeneficiaryDisplayName,
                proposalCommand.ProofRequirement,
                proposalCommand.InvoiceFileId,
                proposalCommand.Confidence,
                [.. proposalCommand.SourceReferences.Select(source =>
                    new PaguaiProposalSourceReference(source.Field, source.Source))]),
            AgentPolicyVersion.Current);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                command.Journey,
                command.Message,
                command.CompanyId,
                command.Attachments,
            })))).ToLowerInvariant();
        var ledger = AgentExecutionLedger.Create(
            Guid.NewGuid(),
            scenario.CompanyId,
            actor.UserId,
            command.Channel,
            command.InboundMessageId,
            command.RequestId,
            command.Journey,
            payloadHash);
        ledger.MarkModelInFlight(actor.UserId, requestId);
        ledger.Complete(JsonSerializer.Serialize(safeOutcome), actor.UserId, requestId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.AgentExecutionLedger.Add(ledger);
        await administrator.SaveChangesAsync();
        return command;
    }

    private static async Task<AgentExecutionResult> ExecuteAgentAsync(
        FoundationApiFactory factory,
        AgentCommand command)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AgentExecutionService>()
            .ExecuteAsync(command, CancellationToken.None);
    }

    private static async Task SetProposalTerminalAsync(
        FoundationApiFactory factory,
        Guid proposalId,
        string terminalState)
    {
        if (terminalState == "expired")
        {
            await SetExpiredByDatabaseClockAsync(factory, proposalId);
            return;
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var proposal = await administrator.PaguaiDraftProposals.SingleAsync(item => item.Id == proposalId);
        var now = proposal.CreatedAt.AddMinutes(1);
        switch (terminalState)
        {
            case "consumed":
                proposal.Consume(Guid.NewGuid(), factory.ActorUserId, Guid.NewGuid(), now);
                break;
            case "rejected":
                proposal.Reject(factory.ActorUserId, Guid.NewGuid(), now);
                break;
            case "invalidated":
                proposal.Invalidate(factory.ActorUserId, Guid.NewGuid(), now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        await administrator.SaveChangesAsync();
    }

    private static async Task<Scenario> SeedScenarioAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Obras", factory.ActorUserId, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Agent Paguai requester", factory.ActorUserId, requestId);
        var capabilityNames = new[]
        {
            KnownCapabilities.AiRead,
            KnownCapabilities.FilesReadOcr,
            KnownCapabilities.PaguaiDraftCreate,
        };
        var capabilities = await administrator.Capabilities
            .Where(capability => capabilityNames.Contains(capability.Name)).ToDictionaryAsync(capability => capability.Name);
        var grants = capabilityNames.Select(name => new RoleCapability(
            Guid.NewGuid(), company.Id, role.Id, capabilities[name].Id, CapabilityScope.Company,
            factory.ActorUserId, requestId)).ToArray();
        var assignment = new RoleAssignment(
            Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, requestId);
        administrator.AddRange(group, company, membership, role, assignment);
        administrator.AddRange(grants);
        await administrator.SaveChangesAsync();
        var file = await SeedAgenticFileAsync(factory, company.Id, membership.Id, "primary");
        return new Scenario(company.Id, membership.Id, role.Id, assignment.Id, file.Id);
    }

    private static async Task<FileRecord> SeedAgenticFileAsync(
        FoundationApiFactory factory,
        Guid companyId,
        Guid membershipId,
        string discriminator)
    {
        var requestId = Guid.NewGuid();
        var file = new FileRecord(
            Guid.NewGuid(), companyId, membershipId, "invoice.pdf", ".pdf", 1024, "application/pdf",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(discriminator))),
            "agentic_attachment", "integration-test", DateTimeOffset.UtcNow.AddDays(1),
            factory.ActorUserId, requestId);
        file.MarkVerified(factory.ActorUserId, requestId);
        file.MarkConfirmed(factory.ActorUserId, requestId);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.Files.Add(file);
        await administrator.SaveChangesAsync();
        return file;
    }

    private static async Task SetExpiredByDatabaseClockAsync(FoundationApiFactory factory, Guid proposalId)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE paguai.draft_proposals
            SET created_at = database_clock.now - interval '16 minutes',
                expires_at = database_clock.now - interval '1 minute'
            FROM (SELECT clock_timestamp() AS now) AS database_clock
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", proposalId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeDependencyAsync(
        FoundationApiFactory factory,
        Scenario scenario,
        string dependency)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        switch (dependency)
        {
            case "membership":
                (await administrator.Memberships.SingleAsync(item => item.Id == scenario.MembershipId))
                    .Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());
                break;
            case "capability":
                (await administrator.RoleAssignments.SingleAsync(item => item.Id == scenario.RoleAssignmentId))
                    .Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());
                break;
            case "file":
                (await administrator.Files.SingleAsync(item => item.Id == scenario.FileId))
                    .MarkDeleted("test_revocation", factory.ActorUserId, Guid.NewGuid());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dependency));
        }

        await administrator.SaveChangesAsync();
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "GRANT EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) TO vinx_runtime;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Scenario(
        Guid CompanyId,
        Guid MembershipId,
        Guid RoleId,
        Guid RoleAssignmentId,
        Guid FileId);
}
