using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Atendimento;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardEndpointTests
{
    [Fact]
    public async Task Creating_a_card_returns_created_and_replays_the_same_request_without_duplication()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var request = new CreateCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            "Acesso bloqueado",
            "Usuário não consegue entrar no portal."
        );

        using var first = await client.PostAsJsonAsync("/atendimento/cards", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.Location);
        Assert.Contains(
            $"/atendimento/cards?companyId={scenario.CompanyId}",
            first.Headers.Location.ToString(),
            StringComparison.Ordinal
        );
        var created = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(created);
        Assert.Equal(scenario.CompanyId, created.CompanyId);
        Assert.Equal(request.Title, created.Title);
        Assert.Equal(request.Description, created.Description);
        Assert.Equal("novo", created.Column);
        Assert.Equal(1, created.Version);
        Assert.Null(created.AttachmentFileId);
        Assert.Empty(created.ParticipantMembershipIds!);

        using var replay = await client.PostAsJsonAsync("/atendimento/cards", request);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(created.Version, replayed.Version);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == request.RequestId
            )
        );
    }

    [Fact]
    public async Task Reusing_a_create_request_with_different_payload_returns_idempotency_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();

        using var first = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var conflict = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Título alterado",
                "Mesmo request_id com payload diferente."
            )
        );

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var error = await conflict.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("idempotency_conflict", error?.Code);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        var denial = await administrator.AuditEvents.SingleAsync(audit =>
            audit.Action == "service_desk.ticket.create"
            && audit.Outcome == AuditOutcome.Denied
            && audit.Reason == "idempotency_conflict"
        );
        Assert.Equal("service_desk.ticket.create", denial.Action);
    }

    [Fact]
    public async Task Create_replays_a_predeploy_receipt_and_rejects_effective_attachment_or_participant_changes()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var legacyRequestId = Guid.NewGuid();
        var legacyCardId = Guid.NewGuid();
        var legacyTitle = "Receipt anterior";
        var legacyDescription = "O formato anterior não tinha anexo nem participantes.";

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.IdempotencyRecords.Add(
                new IdempotencyRecord(
                    Guid.NewGuid(),
                    scenario.CompanyId,
                    factory.ActorUserId,
                    "service_desk.ticket.create",
                    legacyRequestId,
                    HashPayload(
                        new LegacyCreatePayload(
                            scenario.CompanyId,
                            legacyRequestId,
                            legacyTitle,
                            legacyDescription
                        )
                    ),
                    JsonSerializer.Serialize(
                        new
                        {
                            Id = legacyCardId,
                            scenario.CompanyId,
                            Title = legacyTitle,
                            Description = legacyDescription,
                            Column = "novo",
                            Version = 1L,
                            AssigneeMembershipId = (Guid?)null,
                        }
                    )
                )
            );
            await administrator.SaveChangesAsync();
        }

        using var legacyReplay = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                legacyRequestId,
                legacyTitle,
                legacyDescription
            )
        );
        Assert.Equal(HttpStatusCode.OK, legacyReplay.StatusCode);
        var replayed = await legacyReplay.Content.ReadFromJsonAsync<CardResponse>();
        Assert.Equal(legacyCardId, replayed?.Id);
        Assert.Null(replayed?.AttachmentFileId);
        Assert.Empty(replayed?.ParticipantMembershipIds!);

        Guid firstAttachmentId;
        Guid changedAttachmentId;
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            firstAttachmentId = await AddConfirmedFileAsync(
                administrator,
                scenario.CompanyId,
                scenario.AssigneeMembershipId,
                factory.ActorUserId,
                "service_desk_attachment"
            );
            changedAttachmentId = await AddConfirmedFileAsync(
                administrator,
                scenario.CompanyId,
                scenario.AssigneeMembershipId,
                factory.ActorUserId,
                "service_desk_attachment"
            );
            await administrator.SaveChangesAsync();
        }

        var requestId = Guid.NewGuid();
        var first = new CreateCardRequest(
            scenario.CompanyId,
            requestId,
            "Com contexto estendido",
            "O hash inclui anexo e conjunto canônico."
        )
        {
            AttachmentFileId = firstAttachmentId,
            ParticipantMembershipIds = [scenario.AssigneeMembershipId],
        };
        using var created = await client.PostAsJsonAsync("/atendimento/cards", first);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var changedAttachment = await client.PostAsJsonAsync(
            "/atendimento/cards",
            first with { AttachmentFileId = changedAttachmentId }
        );
        Assert.Equal(HttpStatusCode.Conflict, changedAttachment.StatusCode);
        Assert.Equal(
            "idempotency_conflict",
            (await changedAttachment.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        using var changedParticipants = await client.PostAsJsonAsync(
            "/atendimento/cards",
            first with { ParticipantMembershipIds = [scenario.ActorMembershipId] }
        );
        Assert.Equal(HttpStatusCode.Conflict, changedParticipants.StatusCode);
        Assert.Equal(
            "idempotency_conflict",
            (await changedParticipants.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );
    }

    [Fact]
    public async Task Creating_a_card_with_a_confirmed_attachment_and_duplicate_participants_preserves_the_canonical_contract_through_its_lifecycle()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var secondParticipantId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var secondParticipant = new User(secondParticipantId, "Ana", "ana@example.test");
            var secondMembership = new Membership(
                Guid.NewGuid(),
                scenario.CompanyId,
                secondParticipantId,
                factory.ActorUserId,
                Guid.NewGuid()
            );
            var attachment = new FileRecord(
                attachmentId,
                scenario.CompanyId,
                scenario.AssigneeMembershipId,
                "evidencia.pdf",
                ".pdf",
                42,
                "application/pdf",
                "a".PadLeft(64, 'a'),
                "service_desk_attachment",
                "web",
                DateTimeOffset.UtcNow.AddDays(1),
                factory.ActorUserId,
                Guid.NewGuid()
            );
            attachment.MarkVerified(factory.ActorUserId, Guid.NewGuid());
            attachment.MarkConfirmed(factory.ActorUserId, Guid.NewGuid());
            administrator.AddRange(secondParticipant, secondMembership, attachment);
            await administrator.SaveChangesAsync();

            var request = new CreateCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
            {
                AttachmentFileId = attachmentId,
                ParticipantMembershipIds =
                [secondMembership.Id, scenario.AssigneeMembershipId, secondMembership.Id],
            };

            using var response = await client.PostAsJsonAsync("/atendimento/cards", request);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<CardResponse>();
            Assert.NotNull(created);
            Assert.Equal(attachmentId, created.AttachmentFileId);
            var expectedParticipants = new[]
            {
                scenario.AssigneeMembershipId,
                secondMembership.Id,
            }.OrderBy(value => value.ToString("N"), StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedParticipants, created.ParticipantMembershipIds);

            using var replay = await client.PostAsJsonAsync(
                "/atendimento/cards",
                request with
                {
                    ParticipantMembershipIds = new[]
                    {
                        scenario.AssigneeMembershipId,
                        secondMembership.Id,
                    },
                }
            );
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayed = await replay.Content.ReadFromJsonAsync<CardResponse>();
            Assert.Equal(created.Id, replayed?.Id);
            AssertExtendedCardFields(replayed, attachmentId, expectedParticipants);

            using var queue = await client.GetAsync(
                $"/atendimento/cards?companyId={scenario.CompanyId}"
            );
            Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
            var queued = Assert.Single(
                (await queue.Content.ReadFromJsonAsync<List<CardResponse>>())!,
                card => card.Id == created.Id
            );
            AssertExtendedCardFields(queued, attachmentId, expectedParticipants);

            using var assign = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/assign",
                new AssignCardRequest(
                    scenario.CompanyId,
                    Guid.NewGuid(),
                    created.Version,
                    scenario.AssigneeMembershipId
                )
            );
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
            var assigned = await assign.Content.ReadFromJsonAsync<CardResponse>();
            AssertExtendedCardFields(assigned, attachmentId, expectedParticipants);

            using var move = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/move",
                new MoveCardRequest(
                    scenario.CompanyId,
                    Guid.NewGuid(),
                    assigned!.Version,
                    "em_andamento"
                )
            );
            Assert.Equal(HttpStatusCode.OK, move.StatusCode);
            var moved = await move.Content.ReadFromJsonAsync<CardResponse>();
            AssertExtendedCardFields(moved, attachmentId, expectedParticipants);

            using var cancel = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/cancel",
                new CancelCardRequest(
                    scenario.CompanyId,
                    Guid.NewGuid(),
                    moved!.Version,
                    "Encerramento da lifecycle."
                )
            );
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var cancelled = await cancel.Content.ReadFromJsonAsync<CardResponse>();
            AssertExtendedCardFields(cancelled, attachmentId, expectedParticipants);

            using var conflict = await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/move",
                new MoveCardRequest(
                    scenario.CompanyId,
                    Guid.NewGuid(),
                    cancelled!.Version,
                    "concluido"
                )
            );
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var conflictBody = await conflict.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
            AssertExtendedCardFields(conflictBody?.Current, attachmentId, expectedParticipants);

            Assert.Equal(
                2,
                await administrator.Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_card_participants WHERE card_id = {created.Id}"
                ).SingleAsync()
            );
        }
    }

    [Fact]
    public async Task Creating_a_card_with_an_unavailable_attachment_leaves_no_card_or_receipt()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var attachmentId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var attachment = new FileRecord(
                attachmentId,
                scenario.CompanyId,
                scenario.AssigneeMembershipId,
                "quarentena.pdf",
                ".pdf",
                42,
                "application/pdf",
                "b".PadLeft(64, 'b'),
                "service_desk_attachment",
                "web",
                DateTimeOffset.UtcNow.AddDays(1),
                factory.ActorUserId,
                Guid.NewGuid()
            );
            administrator.Files.Add(attachment);
            await administrator.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
            {
                AttachmentFileId = attachmentId,
            }
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("resource_not_found", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await verify.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        Assert.Equal(
            0L,
            await verify.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE company_id = {scenario.CompanyId}"
            ).SingleAsync()
        );
    }

    [Fact]
    public async Task Create_rejects_wrong_company_attachment_and_every_invalid_participant_without_partial_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var requestIds = new List<Guid>();
        Guid wrongClassificationAttachmentId;
        Guid foreignAttachmentId;
        Guid revokedMembershipId;
        Guid foreignMembershipId;

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var foreignCompany = new Company(
                Guid.NewGuid(),
                scenario.GroupId,
                "Empresa estrangeira",
                factory.ActorUserId,
                Guid.NewGuid()
            );
            var foreignUser = new User(Guid.NewGuid(), "Pessoa estrangeira", "foreign@example.test");
            var foreignMembership = new Membership(
                Guid.NewGuid(),
                foreignCompany.Id,
                foreignUser.Id,
                factory.ActorUserId,
                Guid.NewGuid()
            );
            var revokedUser = new User(Guid.NewGuid(), "Pessoa revogada", "revoked@example.test");
            var revokedMembership = new Membership(
                Guid.NewGuid(),
                scenario.CompanyId,
                revokedUser.Id,
                factory.ActorUserId,
                Guid.NewGuid()
            );
            administrator.AddRange(
                foreignCompany,
                foreignUser,
                foreignMembership,
                revokedUser,
                revokedMembership
            );
            await administrator.SaveChangesAsync();
            revokedMembership.Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());

            wrongClassificationAttachmentId = await AddConfirmedFileAsync(
                administrator,
                scenario.CompanyId,
                scenario.AssigneeMembershipId,
                factory.ActorUserId,
                "paguai_proof"
            );
            foreignAttachmentId = await AddConfirmedFileAsync(
                administrator,
                foreignCompany.Id,
                foreignMembership.Id,
                factory.ActorUserId,
                "service_desk_attachment"
            );
            await administrator.SaveChangesAsync();
            revokedMembershipId = revokedMembership.Id;
            foreignMembershipId = foreignMembership.Id;
        }

        var cases = new Func<Guid, CreateCardRequest>[]
        {
            requestId => new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            ) { AttachmentFileId = wrongClassificationAttachmentId },
            requestId => new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            ) { AttachmentFileId = foreignAttachmentId },
            requestId => new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            ) { ParticipantMembershipIds = [revokedMembershipId] },
            requestId => new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            ) { ParticipantMembershipIds = [foreignMembershipId] },
            requestId => new CreateCardRequest(
                scenario.CompanyId,
                requestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            ) { ParticipantMembershipIds = [Guid.NewGuid()] },
        };

        foreach (var create in cases)
        {
            var requestId = Guid.NewGuid();
            requestIds.Add(requestId);
            using var response = await client.PostAsJsonAsync("/atendimento/cards", create(requestId));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("resource_not_found", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        }

        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            0L,
            await verify.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE company_id = {scenario.CompanyId}"
            ).SingleAsync()
        );
        Assert.Equal(
            0L,
            await verify.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_card_participants WHERE company_id = {scenario.CompanyId}"
            ).SingleAsync()
        );
        Assert.False(
            await verify.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && requestIds.Contains(record.RequestId)
            )
        );
    }

    [Fact]
    public async Task Create_rejects_more_than_twenty_five_participants_at_the_model_level()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var oversizedRequestId = Guid.NewGuid();
        var boundedRequestId = Guid.NewGuid();

        using var oversized = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                oversizedRequestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
            {
                ParticipantMembershipIds = Enumerable
                    .Range(0, 26)
                    .Select(_ => Guid.NewGuid())
                    .ToArray(),
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        Assert.Equal(
            "invalid_request",
            (await oversized.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        using var bounded = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                scenario.CompanyId,
                boundedRequestId,
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
            {
                ParticipantMembershipIds = Enumerable
                    .Range(0, 25)
                    .Select(_ => Guid.NewGuid())
                    .ToArray(),
            }
        );

        Assert.NotEqual(HttpStatusCode.BadRequest, bounded.StatusCode);
        Assert.Equal(
            "resource_not_found",
            (await bounded.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.False(
            await administrator.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId
                && (record.RequestId == oversizedRequestId || record.RequestId == boundedRequestId)
            )
        );
    }

    [Fact]
    public async Task Queue_never_exposes_a_card_from_an_unselected_or_inaccessible_company()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var visible = await CreateCardAsync(client, scenario.CompanyId);
        var hiddenCardId = Guid.NewGuid();
        var hiddenCompanyId = Guid.NewGuid();

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.Companies.Add(
                new Company(
                    hiddenCompanyId,
                    scenario.GroupId,
                    "Empresa inacessível",
                    factory.ActorUserId,
                    Guid.NewGuid()
                )
            );
            await administrator.SaveChangesAsync();
            await administrator.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_desk.atendimento_cards
                    (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
                VALUES ({hiddenCardId}, {hiddenCompanyId}, 'Cartão oculto', 'Não pode vazar entre empresas.', 'novo', {factory.ActorUserId}, {factory.ActorUserId}, {Guid.NewGuid()})
                """
            );
        }

        using var selectedQueue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.OK, selectedQueue.StatusCode);
        Assert.True(selectedQueue.Headers.CacheControl?.Private);
        Assert.True(selectedQueue.Headers.CacheControl?.NoStore);
        var cards = await selectedQueue.Content.ReadFromJsonAsync<List<CardResponse>>();
        Assert.Contains(cards!, card => card.Id == visible.Id);
        Assert.DoesNotContain(cards!, card => card.Id == hiddenCardId);

        using var inaccessibleQueue = await client.GetAsync(
            $"/atendimento/cards?companyId={hiddenCompanyId}"
        );
        Assert.Equal(HttpStatusCode.Forbidden, inaccessibleQueue.StatusCode);
        var body = await inaccessibleQueue.Content.ReadAsStringAsync();
        Assert.Contains("membership_required", body, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenCardId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cartão oculto", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_move_capability_is_denied_and_audited_under_the_command_action()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            KnownCapabilities.ServiceDeskTicketReadQueue,
            KnownCapabilities.ServiceDeskTicketCreate,
            KnownCapabilities.ServiceDeskTicketAssign
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var denied = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, "concluido")
        );

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var error = await denied.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("capability_denied", error?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var audit = await administrator.AuditEvents.SingleAsync(candidate =>
            candidate.Action == "service_desk.ticket.move"
            && candidate.Outcome == AuditOutcome.Denied
            && candidate.Reason == "capability_denied"
        );
        Assert.Equal("service_desk.ticket.move", audit.Action);
    }

    [Fact]
    public async Task Missing_cancel_capability_is_denied_even_with_move_and_audited_under_the_cancel_action()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            KnownCapabilities.ServiceDeskTicketReadQueue,
            KnownCapabilities.ServiceDeskTicketCreate,
            KnownCapabilities.ServiceDeskTicketAssign,
            KnownCapabilities.ServiceDeskTicketMove
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var denied = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                created.Version,
                "Cancelamento não autorizado."
            )
        );

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var error = await denied.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("capability_denied", error?.Code);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var audit = await administrator.AuditEvents.SingleAsync(candidate =>
            candidate.Action == "service_desk.ticket.cancel"
            && candidate.Outcome == AuditOutcome.Denied
            && candidate.Reason == "capability_denied"
        );
        Assert.Equal("service_desk.ticket.cancel", audit.Action);
    }

    [Fact]
    public async Task A_stale_move_returns_conflict_without_overwriting_the_current_column()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var moved = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, "em_andamento")
        );
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        using var stale = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, "concluido")
        );
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var cards = await queue.Content.ReadFromJsonAsync<List<CardResponse>>();
        var current = Assert.Single(cards!, card => card.Id == created.Id);
        Assert.Equal("em_andamento", current.Column);
        Assert.Equal(created.Version + 1, current.Version);
    }

    [Fact]
    public async Task Assigning_an_active_membership_updates_the_card_and_replays_the_same_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var request = new AssignCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            created.Version,
            scenario.AssigneeMembershipId
        );

        using var first = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            request
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var assigned = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(assigned);
        Assert.Equal(scenario.AssigneeMembershipId, assigned.AssigneeMembershipId);
        Assert.Equal(created.Version + 1, assigned.Version);

        using var replay = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            request
        );

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(assigned.Id, replayed.Id);
        Assert.Equal(assigned.Version, replayed.Version);
    }

    [Fact]
    public async Task Assignment_options_return_only_active_company_membership_names_in_canonical_order()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var revokedMembershipId = Guid.NewGuid();

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var ana = new User(Guid.NewGuid(), "Ana", "ana-options@example.test");
            var anaMembership = new Membership(
                Guid.NewGuid(),
                scenario.CompanyId,
                ana.Id,
                factory.ActorUserId,
                Guid.NewGuid()
            );
            var revoked = new User(Guid.NewGuid(), "Revogada", "revogada-options@example.test");
            var revokedMembership = new Membership(
                revokedMembershipId,
                scenario.CompanyId,
                revoked.Id,
                factory.ActorUserId,
                Guid.NewGuid()
            );
            administrator.AddRange(ana, anaMembership, revoked, revokedMembership);
            await administrator.SaveChangesAsync();
            revokedMembership.Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        using var response = await client.GetAsync(
            $"/atendimento/assignment-options?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = await response.Content.ReadFromJsonAsync<List<AssignmentOptionResponse>>();
        Assert.NotNull(options);
        Assert.Equal(
            options.OrderBy(option => option.DisplayName, StringComparer.Ordinal)
                .ThenBy(option => option.MembershipId),
            options
        );
        Assert.True(options.Count >= 2);
        Assert.True(
            options.FindIndex(option => option.DisplayName == "Ana")
                < options.FindIndex(option => option.DisplayName == "Bea")
        );
        Assert.Contains(options, option => option.MembershipId == scenario.AssigneeMembershipId && option.DisplayName == "Bea");
        Assert.DoesNotContain(options, option => option.MembershipId == revokedMembershipId);
        Assert.DoesNotContain(options, option => option.DisplayName == "Revogada");
    }

    [Fact]
    public async Task Assignment_options_deny_missing_assign_capability_and_foreign_company_without_returning_people()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            KnownCapabilities.ServiceDeskTicketReadQueue,
            KnownCapabilities.ServiceDeskTicketCreate,
            KnownCapabilities.ServiceDeskTicketMove
        );
        using var client = factory.CreateClient();

        using var denied = await client.GetAsync(
            $"/atendimento/assignment-options?companyId={scenario.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("capability_denied", (await denied.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.Companies.Add(
                new Company(
                    Guid.NewGuid(),
                    scenario.GroupId,
                    "Empresa sem membership",
                    factory.ActorUserId,
                    Guid.NewGuid()
                )
            );
            await administrator.SaveChangesAsync();
            var foreignCompanyId = await administrator.Companies.Where(company =>
                company.GroupId == scenario.GroupId && company.Name == "Empresa sem membership"
            ).Select(company => company.Id).SingleAsync();

            using var foreign = await client.GetAsync(
                $"/atendimento/assignment-options?companyId={foreignCompanyId}"
            );
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            var body = await foreign.Content.ReadAsStringAsync();
            Assert.Contains("membership_required", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Bea", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancelling_a_card_is_terminal_audited_and_replays_before_reading_the_current_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var cancellation = new CancelCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            created.Version,
            "Solicitação duplicada."
        );

        using var first = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            cancellation
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cancelled = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(cancelled);
        Assert.Equal("cancelado", cancelled.Column);
        Assert.Equal(created.Version + 1, cancelled.Version);

        using var replay = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            cancellation
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(cancelled.Id, (await replay.Content.ReadFromJsonAsync<CardResponse>())?.Id);

        using var distinct = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            cancellation with { RequestId = Guid.NewGuid() }
        );
        Assert.Equal(HttpStatusCode.Conflict, distinct.StatusCode);
        var conflict = await distinct.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
        Assert.Equal("terminal_state", conflict?.Code);
        Assert.Equal("cancelado", conflict?.Current?.Column);

        using var move = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), cancelled.Version, "concluido")
        );
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
        var moveConflict = await move.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
        Assert.Equal("cancelado", moveConflict?.Current?.Column);

        using var assign = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            new AssignCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                cancelled.Version,
                scenario.AssigneeMembershipId
            )
        );
        Assert.Equal(HttpStatusCode.Conflict, assign.StatusCode);
        var assignConflict = await assign.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
        Assert.Equal("cancelado", assignConflict?.Current?.Column);

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1L,
            await administrator.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_card_history WHERE card_id = {created.Id} AND from_column = 'novo' AND to_column = 'cancelado'"
            ).SingleAsync()
        );
        Assert.Single(
            await administrator.AuditEvents.Where(audit =>
                audit.Action == "service_desk.ticket.cancel"
                && audit.Outcome == AuditOutcome.Success
                && audit.ResourceId == created.Id
            ).ToListAsync()
        );
    }

    [Fact]
    public async Task Cancel_uses_each_routed_card_as_an_independent_scope_and_rejects_stale_or_foreign_targets()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var firstCard = await CreateCardAsync(client, scenario.CompanyId);
        var secondCard = await CreateCardAsync(client, scenario.CompanyId);
        var sharedRequestId = Guid.NewGuid();

        using var firstCancel = await client.PostAsJsonAsync(
            $"/atendimento/cards/{firstCard.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                sharedRequestId,
                firstCard.Version,
                "Cancelado de forma independente."
            )
        );
        using var secondCancel = await client.PostAsJsonAsync(
            $"/atendimento/cards/{secondCard.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                sharedRequestId,
                secondCard.Version,
                "Cancelado de forma independente."
            )
        );
        Assert.Equal(HttpStatusCode.OK, firstCancel.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondCancel.StatusCode);

        var staleCard = await CreateCardAsync(client, scenario.CompanyId);
        using var moved = await client.PostAsJsonAsync(
            $"/atendimento/cards/{staleCard.Id}/move",
            new MoveCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                staleCard.Version,
                "em_andamento"
            )
        );
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        using var staleCancel = await client.PostAsJsonAsync(
            $"/atendimento/cards/{staleCard.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                staleCard.Version,
                "Versão antiga."
            )
        );
        Assert.Equal(HttpStatusCode.Conflict, staleCancel.StatusCode);
        Assert.Equal(
            "em_andamento",
            (await staleCancel.Content.ReadFromJsonAsync<IntegrityConflictResponse>())?.Current?.Column
        );

        var foreignCompanyId = Guid.NewGuid();
        var foreignCardId = Guid.NewGuid();
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.Companies.Add(
                new Company(
                    foreignCompanyId,
                    scenario.GroupId,
                    "Empresa estrangeira",
                    factory.ActorUserId,
                    Guid.NewGuid()
                )
            );
            await administrator.SaveChangesAsync();
            await administrator.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_desk.atendimento_cards
                    (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
                VALUES ({foreignCardId}, {foreignCompanyId}, 'Cartão externo', 'Não pode aparecer.', 'novo', {factory.ActorUserId}, {factory.ActorUserId}, {Guid.NewGuid()})
                """
            );
        }

        using var foreignCancel = await client.PostAsJsonAsync(
            $"/atendimento/cards/{foreignCardId}/cancel",
            new CancelCardRequest(
                foreignCompanyId,
                Guid.NewGuid(),
                1,
                "Não autorizado."
            )
        );
        Assert.Equal(HttpStatusCode.Forbidden, foreignCancel.StatusCode);
        var foreignBody = await foreignCancel.Content.ReadAsStringAsync();
        Assert.Contains("membership_required", foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignCardId.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cartão externo", foreignBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Foreign_card_id_with_an_authorized_company_context_returns_not_found_without_enumeration()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var foreignCompanyId = Guid.NewGuid();
        var foreignCardId = Guid.NewGuid();

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            administrator.Companies.Add(
                new Company(
                    foreignCompanyId,
                    scenario.GroupId,
                    "Empresa estrangeira autorizada no corpo",
                    factory.ActorUserId,
                    Guid.NewGuid()
                )
            );
            await administrator.SaveChangesAsync();
            await administrator.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO service_desk.atendimento_cards
                    (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
                VALUES ({foreignCardId}, {foreignCompanyId}, 'Cartão externo oculto', 'Não pode ser enumerado.', 'novo', {factory.ActorUserId}, {factory.ActorUserId}, {Guid.NewGuid()})
                """
            );
        }

        using var response = await client.PostAsJsonAsync(
            $"/atendimento/cards/{foreignCardId}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                1,
                "Contexto autorizado da empresa local."
            )
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("resource_not_found", (await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("membership_required", body, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignCardId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cartão externo oculto", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_cancel_audit_rolls_back_card_history_and_receipt_in_a_fresh_context()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var card = await CreateCardAsync(client, scenario.CompanyId);
        var requestId = Guid.NewGuid();
        await RevokeAuditAppendAsync(factory);

        using var response = await client.PostAsJsonAsync(
            $"/atendimento/cards/{card.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                requestId,
                card.Version,
                "Auditoria indisponível."
            )
        );
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        var unchanged = await verify.Database.SqlQuery<CardDatabaseState>(
            $"SELECT column_name AS column, cancelled_at, cancelled_by_user_id, cancellation_reason FROM service_desk.atendimento_cards WHERE id = {card.Id}"
        ).SingleAsync();
        Assert.Equal("novo", unchanged.Column);
        Assert.Null(unchanged.CancelledAt);
        Assert.Null(unchanged.CancelledByUserId);
        Assert.Null(unchanged.CancellationReason);
        Assert.Equal(
            0L,
            await verify.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_card_history WHERE card_id = {card.Id}"
            ).SingleAsync()
        );
        Assert.False(
            await verify.IdempotencyRecords.AnyAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        Assert.False(
            await verify.AuditEvents.AnyAsync(audit =>
                audit.Action == "service_desk.ticket.cancel"
                && audit.Outcome == AuditOutcome.Success
                && audit.ResourceId == card.Id
            )
        );
    }

    [Fact]
    public async Task Assigning_a_revoked_membership_returns_not_found_without_changing_the_card()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var revoked = await administrator.Memberships.SingleAsync(membership =>
                membership.Id == scenario.AssigneeMembershipId
            );
            revoked.Revoke("test_revocation", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            new AssignCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                created.Version,
                scenario.AssigneeMembershipId
            )
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("resource_not_found", error?.Code);
        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        var cards = await queue.Content.ReadFromJsonAsync<List<CardResponse>>();
        var current = Assert.Single(cards!, card => card.Id == created.Id);
        Assert.Null(current.AssigneeMembershipId);
        Assert.Equal(created.Version, current.Version);
    }

    [Fact]
    public async Task Reusing_a_move_request_id_on_distinct_cards_keeps_each_card_in_its_own_idempotency_scope()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var firstCard = await CreateCardAsync(client, scenario.CompanyId);
        var secondCard = await CreateCardAsync(client, scenario.CompanyId);
        var requestId = Guid.NewGuid();

        using var first = await client.PostAsJsonAsync(
            $"/atendimento/cards/{firstCard.Id}/move",
            new MoveCardRequest(scenario.CompanyId, requestId, firstCard.Version, "em_andamento")
        );
        using var second = await client.PostAsJsonAsync(
            $"/atendimento/cards/{secondCard.Id}/move",
            new MoveCardRequest(scenario.CompanyId, requestId, secondCard.Version, "em_andamento")
        );

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var movedFirst = await first.Content.ReadFromJsonAsync<CardResponse>();
        var movedSecond = await second.Content.ReadFromJsonAsync<CardResponse>();
        Assert.Equal(firstCard.Id, movedFirst?.Id);
        Assert.Equal(secondCard.Id, movedSecond?.Id);
        Assert.Equal("em_andamento", movedSecond?.Column);
    }

    [Fact]
    public async Task Concurrent_create_reconciles_only_the_shared_idempotency_record()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var request = new CreateCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            "Criação concorrente",
            "O mesmo comando deve criar um cartão uma vez."
        );
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = SendCreateAsync(firstClient);
        var second = SendCreateAsync(secondClient);
        start.SetResult();
        var responses = await Task.WhenAll(first, second);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var cards = await Task.WhenAll(
                responses.Select(response => response.Content.ReadFromJsonAsync<CardResponse>())
            );
            Assert.Equal(cards[0]?.Id, cards[1]?.Id);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            1,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == request.RequestId
            )
        );
        Assert.Equal(
            1L,
            await administrator
                .Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE request_id = {request.RequestId}"
                )
                .SingleAsync()
        );

        async Task<HttpResponseMessage> SendCreateAsync(HttpClient client)
        {
            await start.Task;
            return await client.PostAsJsonAsync("/atendimento/cards", request);
        }
    }

    [Fact]
    public async Task Concurrent_moves_return_one_success_and_one_current_state_from_the_actual_winner()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var createClient = factory.CreateClient();
        var created = await CreateCardAsync(createClient, scenario.CompanyId);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = SendMoveAsync(firstClient, "em_andamento");
        var second = SendMoveAsync(secondClient, "concluido");
        start.SetResult();
        var responses = await Task.WhenAll(first, second);
        try
        {
            var success = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.OK
            );
            var conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict
            );
            var winner = await success.Content.ReadFromJsonAsync<CardResponse>();
            var stale = await conflict.Content.ReadFromJsonAsync<IntegrityConflictResponse>();

            Assert.NotNull(winner);
            Assert.Equal("integrity_conflict", stale?.Code);
            Assert.NotNull(stale?.Current);
            Assert.Equal(winner.Id, stale!.Current!.Id);
            Assert.Equal(winner.Column, stale.Current.Column);
            Assert.Equal(winner.Version, stale.Current.Version);
            Assert.Equal(created.Version + 1, stale.Current.Version);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var history = new NpgsqlCommand(
            "SELECT count(*) FROM service_desk.atendimento_card_history WHERE card_id = @card_id",
            administrator
        );
        history.Parameters.AddWithValue("card_id", created.Id);
        Assert.Equal(1L, await history.ExecuteScalarAsync());

        async Task<HttpResponseMessage> SendMoveAsync(HttpClient client, string column)
        {
            await start.Task;
            return await client.PostAsJsonAsync(
                $"/atendimento/cards/{created.Id}/move",
                new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, column)
            );
        }
    }

    [Fact]
    public Task Concurrent_cancel_vs_move_has_one_winner_and_leaves_a_terminal_card_immutable() =>
        AssertConcurrentCancellationRaceAsync(CancellationRaceCompetitor.Move);

    [Fact]
    public Task Concurrent_cancel_vs_assign_has_one_winner_and_leaves_a_terminal_card_immutable() =>
        AssertConcurrentCancellationRaceAsync(CancellationRaceCompetitor.Assign);

    [Fact]
    public Task Concurrent_cancel_vs_cancel_has_one_winner_and_leaves_a_terminal_card_immutable() =>
        AssertConcurrentCancellationRaceAsync(CancellationRaceCompetitor.Cancel);

    [Fact]
    public async Task Failed_success_audit_rolls_back_card_and_idempotency_record()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        await RevokeAuditAppendAsync(factory);
        var requestId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AtendimentoCardService>();
            var result = await service.CreateAsync(
                new ActorContext(factory.ActorUserId, factory.CorrelationId),
                new CreateAtendimentoCardCommand(
                    scenario.CompanyId,
                    requestId,
                    "Auditoria indisponível",
                    "Nenhuma mutação pode sobreviver sem auditoria."
                ),
                CancellationToken.None
            );

            Assert.Equal(AtendimentoCardOperationState.Unavailable, result.State);
        }

        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            0,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId && record.RequestId == requestId
            )
        );
        Assert.Equal(
            0L,
            await administrator
                .Database.SqlQuery<long>(
                    $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_cards WHERE company_id = {scenario.CompanyId}"
                )
                .SingleAsync()
        );
    }

    [Fact]
    public async Task Browser_cookie_card_mutation_requires_csrf_and_succeeds_with_a_fresh_bff_token()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
            ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid));
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        using var signIn = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var create = new CreateCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            "Sessão BFF",
            "A mutação do cookie exige antiforgery."
        );

        using var missingCsrf = await client.PostAsJsonAsync("/atendimento/cards", create);

        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Contains("invalid_request", await missingCsrf.Content.ReadAsStringAsync());

        var csrf = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/atendimento/cards")
        {
            Content = JsonContent.Create(create),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var created = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.Private);
        Assert.True(created.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Browser_cookie_cancel_requires_csrf_and_succeeds_with_a_fresh_bff_token()
    {
        var provider = new BrowserSessionProviderHandler();
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ => ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid));
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        using var signIn = await SignInAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var createCsrf = await GetAntiforgeryTokenAsync(client);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/atendimento/cards")
        {
            Content = JsonContent.Create(
                new CreateCardRequest(
                    scenario.CompanyId,
                    Guid.NewGuid(),
                    "Sessão BFF",
                    "O cancelamento também exige antiforgery."
                )
            ),
        };
        createRequest.Headers.Add("X-CSRF-TOKEN", createCsrf);
        using var createResponse = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<CardResponse>())!;
        var cancellation = new CancelCardRequest(
            scenario.CompanyId,
            Guid.NewGuid(),
            created.Version,
            "Solicitação duplicada."
        );

        using var missingCsrf = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            cancellation
        );
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var csrf = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/atendimento/cards/{created.Id}/cancel"
        )
        {
            Content = JsonContent.Create(cancellation),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var cancelled = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    [Fact]
    public async Task Openapi_marks_each_atendimento_mutation_with_the_bff_antiforgery_header_and_conflict_schema()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (
            var path in new[]
            {
                "/atendimento/cards",
                "/atendimento/cards/{id}/assign",
                "/atendimento/cards/{id}/move",
                "/atendimento/cards/{id}/cancel",
            }
        )
        {
            var post = paths.GetProperty(path).GetProperty("post");
            var csrf = post.GetProperty("parameters")
                .EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "X-CSRF-TOKEN");
            Assert.Equal("header", csrf.GetProperty("in").GetString());
            Assert.False(
                csrf.TryGetProperty("required", out var required) && required.GetBoolean()
            );
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var createPost = paths.GetProperty("/atendimento/cards").GetProperty("post");
        Assert.True(createPost.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(
            HasSchemaType(
                createPost
                    .GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema"),
                "object"
            )
        );
        var createProperties = schemas
            .GetProperty("CreateAtendimentoCardRequest")
            .GetProperty("properties");
        Assert.True(IsNullableSchema(createProperties.GetProperty("attachmentFileId")));
        Assert.True(HasSchemaType(createProperties.GetProperty("participantMembershipIds"), "array"));
        var cardProperties = schemas
            .GetProperty("AtendimentoCardResponse")
            .GetProperty("properties");
        Assert.True(IsNullableSchema(cardProperties.GetProperty("attachmentFileId")));
        Assert.True(HasSchemaType(cardProperties.GetProperty("participantMembershipIds"), "array"));
        var cancelPost = paths.GetProperty("/atendimento/cards/{id}/cancel").GetProperty("post");
        Assert.True(
            HasSchemaType(
                cancelPost
                    .GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema"),
                "object"
            )
        );
        var cancelProperties = schemas
            .GetProperty("CancelAtendimentoCardRequest")
            .GetProperty("properties");
        Assert.True(HasSchemaType(cancelProperties.GetProperty("reason"), "string"));
        Assert.True(HasSchemaType(cancelProperties.GetProperty("expectedVersion"), "integer"));
        foreach (
            var path in new[]
            {
                "/atendimento/cards/{id}/assign",
                "/atendimento/cards/{id}/move",
                "/atendimento/cards/{id}/cancel",
            }
        )
        {
            var conflictSchema = paths
                .GetProperty(path)
                .GetProperty("post")
                .GetProperty("responses")
                .GetProperty("409")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema");
            Assert.Equal(
                "#/components/schemas/AtendimentoCardConflictResponse",
                conflictSchema.GetProperty("$ref").GetString()
            );
        }

        var conflictProperties = schemas
            .GetProperty("AtendimentoCardConflictResponse")
            .GetProperty("properties");
        Assert.Equal(
            "string",
            conflictProperties.GetProperty("code").GetProperty("type").GetString()
        );
        Assert.True(IsNullableSchema(conflictProperties.GetProperty("current")));

        var assignmentOptions = paths.GetProperty("/atendimento/assignment-options").GetProperty("get");
        Assert.Equal(
            "#/components/schemas/AtendimentoAssignmentOptionResponse",
            assignmentOptions
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("items")
                .GetProperty("$ref")
                .GetString()
        );
    }

    private static readonly string[] sourceArray = new[] { "anyOf", "oneOf" };

    private static bool IsNullableSchema(JsonElement schema)
    {
        if (
            schema.TryGetProperty("nullable", out var nullable)
            && nullable.ValueKind == JsonValueKind.True
        )
        {
            return true;
        }

        if (
            schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Array
            && type.EnumerateArray().Any(value => value.GetString() == "null")
        )
        {
            return true;
        }

        return sourceArray.Any(composition =>
            schema.TryGetProperty(composition, out var alternatives)
            && alternatives
                .EnumerateArray()
                .Any(alternative =>
                    alternative.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "null"
                )
        );
    }

    private static bool HasSchemaType(JsonElement schema, string expectedType) =>
        schema.TryGetProperty("type", out var type)
        && (type.ValueKind == JsonValueKind.String
            ? type.GetString() == expectedType
            : type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(value => value.GetString() == expectedType));

    private static void AssertExtendedCardFields(
        CardResponse? card,
        Guid attachmentFileId,
        IReadOnlyList<Guid> participantMembershipIds
    )
    {
        Assert.NotNull(card);
        Assert.Equal(attachmentFileId, card!.AttachmentFileId);
        Assert.Equal(participantMembershipIds, card.ParticipantMembershipIds);
    }

    private static async Task AssertConcurrentCancellationRaceAsync(
        CancellationRaceCompetitor competitor
    )
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var createClient = factory.CreateClient();
        var card = await CreateCardAsync(createClient, scenario.CompanyId);
        using var cancellationClient = factory.CreateClient();
        using var competingClient = factory.CreateClient();
        var cancellationRequestId = Guid.NewGuid();
        var competingRequestId = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancellation = SendCancellationAsync(cancellationClient, cancellationRequestId);
        var competing = SendCompetingMutationAsync(
            competingClient,
            competitor,
            competingRequestId,
            card.Version
        );
        start.SetResult();
        var responses = await Task.WhenAll(cancellation, competing);
        CardResponse winner;
        IntegrityConflictResponse loser;
        try
        {
            var success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict
            );
            winner = (await success.Content.ReadFromJsonAsync<CardResponse>())!;
            loser = (await conflict.Content.ReadFromJsonAsync<IntegrityConflictResponse>())!;
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        if (competitor == CancellationRaceCompetitor.Cancel)
        {
            // A segunda tentativa de cancelamento pode perder a corrida pela
            // leitura terminal (terminal_state) ou pela concorrência otimista
            // (integrity_conflict); ambas são 409 sem sobrescrever o vencedor.
            Assert.Contains(loser.Code, new[] { "integrity_conflict", "terminal_state" });
        }
        else
        {
            Assert.Equal("integrity_conflict", loser.Code);
        }
        Assert.NotNull(loser.Current);
        Assert.Equal(winner.Id, loser.Current!.Id);
        Assert.Equal(winner.Column, loser.Current.Column);
        Assert.Equal(winner.Version, loser.Current.Version);
        Assert.Equal(card.Version + 1, winner.Version);

        var cancellationWon = winner.Column == "cancelado";
        var terminalRequestId = cancellationWon ? cancellationRequestId : Guid.NewGuid();
        CardResponse terminal;
        if (cancellationWon)
        {
            terminal = winner;
        }
        else
        {
            using var terminalResponse = await cancellationClient.PostAsJsonAsync(
                $"/atendimento/cards/{card.Id}/cancel",
                new CancelCardRequest(
                    scenario.CompanyId,
                    terminalRequestId,
                    winner.Version,
                    "Cancelamento após o conflito."
                )
            );
            Assert.Equal(HttpStatusCode.OK, terminalResponse.StatusCode);
            terminal = (await terminalResponse.Content.ReadFromJsonAsync<CardResponse>())!;
        }

        Assert.Equal("cancelado", terminal.Column);
        using (var afterTerminal = await SendCompetingMutationAsync(
            competingClient,
            competitor,
            Guid.NewGuid(),
            terminal.Version
        ))
        {
            Assert.Equal(HttpStatusCode.Conflict, afterTerminal.StatusCode);
            var conflict = await afterTerminal.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
            Assert.Equal(
                competitor == CancellationRaceCompetitor.Cancel
                    ? "terminal_state"
                    : "integrity_conflict",
                conflict?.Code
            );
            Assert.Equal("cancelado", conflict?.Current?.Column);
            Assert.Equal(terminal.Version, conflict?.Current?.Version);
        }

        var initialHistoryCount = cancellationWon || competitor == CancellationRaceCompetitor.Move
            ? 1L
            : 0L;
        var expectedSuccesses = cancellationWon ? 1 : 2;
        var expectedHistoryCount = initialHistoryCount + (cancellationWon ? 0 : 1);
        var racedAndTerminalRequestIds = cancellationWon
            ? new[] { cancellationRequestId, competingRequestId }
            : new[] { cancellationRequestId, competingRequestId, terminalRequestId };
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(
            expectedSuccesses,
            await administrator.IdempotencyRecords.CountAsync(record =>
                record.CompanyId == scenario.CompanyId
                && racedAndTerminalRequestIds.Contains(record.RequestId)
            )
        );
        Assert.Equal(
            expectedHistoryCount,
            await administrator.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM service_desk.atendimento_card_history WHERE card_id = {card.Id}"
            ).SingleAsync()
        );
        var successfulActions = competitor switch
        {
            CancellationRaceCompetitor.Move =>
                new[] { "service_desk.ticket.cancel", "service_desk.ticket.move" },
            CancellationRaceCompetitor.Assign =>
                new[] { "service_desk.ticket.cancel", "service_desk.ticket.assign" },
            CancellationRaceCompetitor.Cancel => new[] { "service_desk.ticket.cancel" },
            _ => throw new ArgumentOutOfRangeException(nameof(competitor), competitor, null),
        };
        Assert.Equal(
            expectedSuccesses,
            await administrator.AuditEvents.CountAsync(audit =>
                audit.ResourceId == card.Id
                && audit.Outcome == AuditOutcome.Success
                && successfulActions.Contains(audit.Action)
            )
        );

        async Task<HttpResponseMessage> SendCancellationAsync(HttpClient client, Guid requestId)
        {
            await start.Task;
            return await client.PostAsJsonAsync(
                $"/atendimento/cards/{card.Id}/cancel",
                new CancelCardRequest(
                    scenario.CompanyId,
                    requestId,
                    card.Version,
                    "Cancelamento concorrente."
                )
            );
        }

        async Task<HttpResponseMessage> SendCompetingMutationAsync(
            HttpClient client,
            CancellationRaceCompetitor operation,
            Guid requestId,
            long expectedVersion
        )
        {
            await start.Task;
            return operation switch
            {
                CancellationRaceCompetitor.Move => await client.PostAsJsonAsync(
                    $"/atendimento/cards/{card.Id}/move",
                    new MoveCardRequest(
                        scenario.CompanyId,
                        requestId,
                        expectedVersion,
                        "em_andamento"
                    )
                ),
                CancellationRaceCompetitor.Assign => await client.PostAsJsonAsync(
                    $"/atendimento/cards/{card.Id}/assign",
                    new AssignCardRequest(
                        scenario.CompanyId,
                        requestId,
                        expectedVersion,
                        scenario.AssigneeMembershipId
                    )
                ),
                CancellationRaceCompetitor.Cancel => await client.PostAsJsonAsync(
                    $"/atendimento/cards/{card.Id}/cancel",
                    new CancelCardRequest(
                        scenario.CompanyId,
                        requestId,
                        expectedVersion,
                        "Cancelamento concorrente alternativo."
                    )
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };
        }
    }

    private static async Task<CardResponse> CreateCardAsync(HttpClient client, Guid companyId)
    {
        using var response = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                companyId,
                Guid.NewGuid(),
                "Acesso bloqueado",
                "Usuário não consegue entrar no portal."
            )
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardResponse>())!;
    }

    private static Task<Guid> AddConfirmedFileAsync(
        FoundationDbContext database,
        Guid companyId,
        Guid uploaderMembershipId,
        Guid actorUserId,
        string classification
    )
    {
        var fileId = Guid.NewGuid();
        var file = new FileRecord(
            fileId,
            companyId,
            uploaderMembershipId,
            "evidencia.pdf",
            ".pdf",
            42,
            "application/pdf",
            Convert.ToHexString(Guid.NewGuid().ToByteArray()).PadLeft(64, 'a'),
            classification,
            "web",
            DateTimeOffset.UtcNow.AddDays(1),
            actorUserId,
            Guid.NewGuid()
        );
        file.MarkVerified(actorUserId, Guid.NewGuid());
        file.MarkConfirmed(actorUserId, Guid.NewGuid());
        database.Files.Add(file);
        return Task.FromResult(fileId);
    }

    private static string HashPayload<T>(T payload) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))))
            .ToLowerInvariant();

    private static async Task<AtendimentoScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        params string[] grantedCapabilities
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Atendimento",
            factory.ActorUserId,
            requestId
        );
        var actorMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var assignee = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var assigneeMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            assignee.Id,
            factory.ActorUserId,
            requestId
        );
        var role = new Role(
            Guid.NewGuid(),
            company.Id,
            "Atendimento administrator",
            factory.ActorUserId,
            requestId
        );
        var capabilityNames =
            grantedCapabilities.Length == 0
                ?
                [
                    KnownCapabilities.ServiceDeskTicketReadQueue,
                    KnownCapabilities.ServiceDeskTicketCreate,
                    KnownCapabilities.ServiceDeskTicketAssign,
                    KnownCapabilities.ServiceDeskTicketMove,
                    KnownCapabilities.ServiceDeskTicketCancel,
                ]
                : grantedCapabilities;
        var capabilities = await administrator
            .Capabilities.Where(capability => capabilityNames.Contains(capability.Name))
            .ToArrayAsync();
        Assert.Equal(capabilityNames.Length, capabilities.Length);
        var assignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            role.Id,
            actorMembership.Id,
            factory.ActorUserId,
            requestId
        );

        administrator.AddRange(
            group,
            company,
            assignee,
            actorMembership,
            assigneeMembership,
            role,
            assignment
        );
        administrator.RoleCapabilities.AddRange(
            capabilities.Select(capability => new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                capability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            ))
        );
        await administrator.SaveChangesAsync();
        return new AtendimentoScenario(
            group.Id,
            company.Id,
            actorMembership.Id,
            assigneeMembership.Id
        );
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var csrf = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session"
        )
        {
            Content = JsonContent.Create(
                new { email = "same-address@example.test", password = "safe-test-password" }
            ),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery"
        );
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The test antiforgery token was empty.");
    }

    private static HttpResponseMessage ProviderResponse(string accessToken) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = accessToken }) };

    private sealed record AtendimentoScenario(
        Guid GroupId,
        Guid CompanyId,
        Guid ActorMembershipId,
        Guid AssigneeMembershipId
    );

    private sealed record CreateCardRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    )
    {
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )]
        public Guid? AttachmentFileId { get; init; }

        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )]
        public IReadOnlyList<Guid>? ParticipantMembershipIds { get; init; }
    }

    private sealed record LegacyCreatePayload(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record MoveCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string Column
    );

    private sealed record AssignCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        Guid AssigneeMembershipId
    );

    private sealed record CancelCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string Reason
    );

    private sealed record AssignmentOptionResponse(Guid MembershipId, string DisplayName);

    private sealed record CardResponse(
        Guid Id,
        Guid CompanyId,
        string Title,
        string Description,
        string Column,
        long Version,
        Guid? AssigneeMembershipId,
        Guid? AttachmentFileId = null,
        IReadOnlyList<Guid>? ParticipantMembershipIds = null
    );

    private sealed record ErrorResponse(string Code);

    private sealed record IntegrityConflictResponse(string Code, CardResponse? Current);

    private sealed record CardDatabaseState(
        string Column,
        DateTimeOffset? CancelledAt,
        Guid? CancelledByUserId,
        string? CancellationReason
    );

    private enum CancellationRaceCompetitor
    {
        Move,
        Assign,
        Cancel,
    }

    private sealed class BrowserSessionProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ResponseFactory?.Invoke(request)
                    ?? throw new InvalidOperationException(
                        "The browser session provider response was not configured."
                    )
            );
    }
}
