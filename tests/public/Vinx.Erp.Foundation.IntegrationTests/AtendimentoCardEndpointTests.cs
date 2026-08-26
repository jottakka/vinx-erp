using System.Net;
using System.Net.Http.Json;
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
        var created = await first.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(created);
        Assert.Equal(scenario.CompanyId, created.CompanyId);
        Assert.Equal(request.Title, created.Title);
        Assert.Equal(request.Description, created.Description);
        Assert.Equal("novo", created.Column);
        Assert.Equal(1, created.Version);

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
    public async Task Creating_a_card_with_a_confirmed_attachment_and_duplicate_participants_returns_the_canonical_contract()
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
            Assert.Equal(
                new[]
                {
                    scenario.AssigneeMembershipId,
                    secondMembership.Id,
                }.OrderBy(value => value.ToString("N"), StringComparer.Ordinal),
                created.ParticipantMembershipIds
            );

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
        Assert.Contains(options, option => option.MembershipId == scenario.AssigneeMembershipId && option.DisplayName == "Bea");
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
        Assert.Equal("integrity_conflict", conflict?.Code);
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
        return new AtendimentoScenario(group.Id, company.Id, assigneeMembership.Id);
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
