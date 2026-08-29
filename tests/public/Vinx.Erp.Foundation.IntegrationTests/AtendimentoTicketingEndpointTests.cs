using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Fatia P6 de usabilidade do Atendimento: campos de ticket (prioridade,
/// categoria, canal, SLA e solicitante), watchers com intents de notificação
/// determinísticas, timeline de eventos, filtros server-side e picker de
/// pessoas (<c>directory.read</c> menor que <c>foundation.employees.create</c>).
/// Os nomes de capabilities novas são literais de propósito: o commit é RED
/// enquanto o catálogo e os endpoints não existirem.
/// </summary>
public sealed class AtendimentoTicketingEndpointTests
{
    private const string FieldsCapability = "service_desk.ticket.fields";
    private const string LookupAdminCapability = "service_desk.lookup.admin";
    private const string DirectoryReadCapability = "directory.read";

    // ---------- Success ----------

    [Fact]
    public async Task Ticket_fields_roundtrip_update_and_survive_the_queue_read()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var categoryId = await InsertCategoryRowAsync(
            factory,
            scenario.CompanyId,
            "Suporte"
        );
        var channelId = await LookupIdAsync(
            factory,
            scenario.CompanyId,
            "service_desk.atendimento_channels",
            "key",
            "whatsapp"
        );

        using var updated = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                created.Version,
                Priority: "alta",
                CategoryRefId: categoryId,
                ChannelRefId: channelId,
                SlaDueAt: DateTimeOffset.UtcNow.AddHours(4),
                RequesterPersonRef: "persona-123",
                MentionedMembershipIds: null
            )
        );

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var card = await updated.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(card);
        Assert.Equal("alta", card!.Priority);
        Assert.Equal(categoryId, card.CategoryRefId);
        Assert.Equal(channelId, card.ChannelRefId);
        Assert.NotNull(card.SlaDueAt);
        Assert.False(card.IsOverdue);
        Assert.Equal("persona-123", card.RequesterPersonRef);

        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        queue.EnsureSuccessStatusCode();
        var queued = Assert.Single((await queue.Content.ReadFromJsonAsync<List<CardResponse>>())!);
        Assert.Equal("alta", queued.Priority);
        Assert.Equal(channelId, queued.ChannelRefId);

        using var lookupsResponse = await client.GetAsync(
            $"/atendimento/lookups?companyId={scenario.CompanyId}"
        );
        lookupsResponse.EnsureSuccessStatusCode();
        var lookups = await lookupsResponse.Content.ReadFromJsonAsync<LookupsResponse>();
        Assert.NotNull(lookups);
        Assert.Contains(lookups!.Categories, category => category.Id == categoryId && category.Name == "Suporte");
        Assert.Equal(
            new[] { "email", "manual", "web", "whatsapp" },
            lookups.Channels.Select(channel => channel.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray()
        );
    }

    [Fact]
    public async Task Ticket_timeline_lists_each_mutation_in_order_with_actor_names()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var assigned = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            new AssignCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.AssigneeMembershipId)
        );
        assigned.EnsureSuccessStatusCode();

        using var timeline = await client.GetAsync(
            $"/atendimento/cards/{created.Id}/events?companyId={scenario.CompanyId}"
        );

        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        var events = await timeline.Content.ReadFromJsonAsync<List<TimelineEventResponse>>();
        Assert.NotNull(events);
        Assert.Equal(
            new[] { "created", "assigned" },
            events!.Select(@event => @event.ActionKind).ToArray()
        );
        Assert.All(events, @event => Assert.False(string.IsNullOrWhiteSpace(@event.ActorDisplayName)));
        Assert.All(events, @event => Assert.NotEqual(default, @event.OccurredAt));
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Unknown_priority_unknown_refs_and_past_sla_are_typed_rejections()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var unknownRef = Guid.NewGuid();

        using var badPriority = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: "urgente", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, badPriority.StatusCode);
        Assert.Equal("invalid_request", (await badPriority.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        using var unknownCategory = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: unknownRef, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.NotFound, unknownCategory.StatusCode);
        Assert.Equal(
            "resource_not_found",
            (await unknownCategory.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        using var pastSla = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: DateTimeOffset.UtcNow.AddMinutes(-5),
                RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, pastSla.StatusCode);
        Assert.Equal("invalid_request", (await pastSla.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        // Nenhuma das tentativas alterou o cartão.
        using var queue = await client.GetAsync($"/atendimento/cards?companyId={scenario.CompanyId}");
        queue.EnsureSuccessStatusCode();
        var untouched = Assert.Single((await queue.Content.ReadFromJsonAsync<List<CardResponse>>())!);
        // O default de criação do main (media) permanece; a rejeição não
        // alterou o cartão.
        Assert.Equal("media", untouched.Priority);
        Assert.Equal(created.Version, untouched.Version);
    }

    [Fact]
    public async Task Lookup_category_crud_is_gated_and_validated()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var restricted = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
            ],
            "Vinx Sem Admin"
        );
        using var restrictedClient = factory.CreateClient();

        using var denied = await restrictedClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(restricted.CompanyId, Guid.NewGuid(), "Financeiro")
        );
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("capability_denied", (await denied.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        var elevated = await SeedScenarioAsync(
            factory,
            [LookupAdminCapability],
            "Vinx Com Admin"
        );
        using var adminClient = factory.CreateClient();
        var requestId = Guid.NewGuid();
        using var first = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, requestId, "Financeiro")
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var category = await first.Content.ReadFromJsonAsync<CategoryResponse>();
        Assert.Equal("Financeiro", category?.Name);

        // Corpo não aparado é recusado antes de qualquer consulta.
        using var spacedName = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, Guid.NewGuid(), " Financeiro ")
        );
        Assert.Equal(HttpStatusCode.BadRequest, spacedName.StatusCode);

        // Mesmo nome aparado com outro requestId é conflito estável.
        using var duplicate = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, Guid.NewGuid(), "Financeiro")
        );
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("lookup_conflict", (await duplicate.Content.ReadFromJsonAsync<ErrorResponse>())?.Code);

        using var replay = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, requestId, "Financeiro")
        );
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<CategoryResponse>();
        Assert.Equal(category!.Id, replayed!.Id);
    }

    // ---------- Access ----------

    [Fact]
    public async Task Cross_company_fields_watchers_and_timeline_never_leak()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var mine = await SeedScenarioAsync(factory, null, "Empresa Minha");
        var other = await SeedScenarioAsync(factory, null, "Empresa Alheia");
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, mine.CompanyId);

        // O ator compartilhado possui membership nas duas empresas semeadas;
        // trocar a empresa muda somente o escopo do cartão → 404, nunca dados.
        using var foreignFields = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                other.CompanyId, Guid.NewGuid(), created.Version,
                Priority: "alta", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.NotFound, foreignFields.StatusCode);

        using var foreignWatcher = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/add",
            new WatcherRequest(other.CompanyId, Guid.NewGuid(), created.Version, other.AssigneeMembershipId)
        );
        Assert.Equal(HttpStatusCode.NotFound, foreignWatcher.StatusCode);

        using var foreignTimeline = await client.GetAsync(
            $"/atendimento/cards/{created.Id}/events?companyId={other.CompanyId}"
        );
        Assert.Equal(HttpStatusCode.NotFound, foreignTimeline.StatusCode);
        var body = await foreignTimeline.Content.ReadAsStringAsync();
        Assert.Contains("resource_not_found", body, StringComparison.Ordinal);
    }

    // ---------- State ----------

    [Fact]
    public async Task Overdue_is_derived_only_while_open_and_sla_has_passed()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        var channelId = await LookupIdAsync(
            factory,
            scenario.CompanyId,
            "service_desk.atendimento_channels",
            "key",
            "web"
        );

        using var scheduled = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: "media", CategoryRefId: null, ChannelRefId: channelId,
                SlaDueAt: DateTimeOffset.UtcNow.AddHours(1),
                RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        scheduled.EnsureSuccessStatusCode();

        // O SLA vence com o tempo real; o teste retroage a coluna para obter
        // o estado derivado determinístico sem relógio controlável.
        await BackdateSlaAsync(factory, created.Id);

        using var overdueQueue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&overdue=true"
        );
        overdueQueue.EnsureSuccessStatusCode();
        Assert.Contains(
            (await overdueQueue.Content.ReadFromJsonAsync<List<CardResponse>>())!,
            card => card.Id == created.Id && card.IsOverdue
        );

        using var moved = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/move",
            new MoveCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version + 1, "concluido")
        );
        moved.EnsureSuccessStatusCode();

        using var finalQueue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&overdue=true"
        );
        finalQueue.EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            (await finalQueue.Content.ReadFromJsonAsync<List<CardResponse>>())!,
            card => card.Id == created.Id
        );
    }

    [Fact]
    public async Task Watchers_cover_repeat_absent_and_stale_paths_without_side_effects()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        const string staleConflict = "integrity_conflict";

        using var added = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/add",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.AssigneeMembershipId)
        );
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var withWatcher = await added.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(withWatcher);
        Assert.Contains(scenario.AssigneeMembershipId, withWatcher!.WatcherMembershipIds!);
        Assert.Equal(created.Version + 1, withWatcher.Version);

        // Repetição de adição: sem DELETE acidental e sem bump de versão.
        using var repeated = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/add",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), withWatcher.Version, scenario.AssigneeMembershipId)
        );
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var unchanged = await repeated.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(unchanged);
        Assert.Equal(withWatcher.Version, unchanged!.Version);
        Assert.Contains(scenario.AssigneeMembershipId, unchanged.WatcherMembershipIds!);

        // Remoção de vínculo ausente: idempotente, sem INSERT e sem bump.
        using var absentRemove = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/remove",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), unchanged.Version, scenario.OtherMembershipId!)
        );
        Assert.Equal(HttpStatusCode.OK, absentRemove.StatusCode);
        var afterAbsent = await absentRemove.Content.ReadFromJsonAsync<CardResponse>();
        Assert.NotNull(afterAbsent);
        Assert.Equal(unchanged.Version, afterAbsent!.Version);
        Assert.Single(afterAbsent.WatcherMembershipIds!, scenario.AssigneeMembershipId);

        // Conflito de versão no caminho de adição preserva estado atual.
        // Conflito na ADIÇÃO usa membro ausente: presença curto-circuita em
        // NoChange antes do bump otimista; versão antiga devolve 409 sem inserir.
        using var staleAdd = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/add",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.OtherMembershipId)
        );
        Assert.Equal(HttpStatusCode.Conflict, staleAdd.StatusCode);
        Assert.Equal(staleConflict, (await staleAdd.Content.ReadFromJsonAsync<IntegrityConflictResponse>())?.Code);

        // Conflito de versão no caminho de remoção também não altera nada.
        using var staleRemove = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/remove",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.AssigneeMembershipId)
        );
        Assert.Equal(HttpStatusCode.Conflict, staleRemove.StatusCode);

        // No-op com versão obsoleta também é conflito: repetição de adição.
        using var staleRepeat = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/add",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.AssigneeMembershipId)
        );
        Assert.Equal(HttpStatusCode.Conflict, staleRepeat.StatusCode);

        // No-op com versão obsoleta: remoção de vínculo ausente.
        using var staleAbsentRemove = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/watchers/remove",
            new WatcherRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.OtherMembershipId!)
        );
        Assert.Equal(HttpStatusCode.Conflict, staleAbsentRemove.StatusCode);

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM service_desk.atendimento_card_watchers WHERE card_id = @card_id",
            administrator
        );
        command.Parameters.AddWithValue("card_id", created.Id);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Runtime_rls_reads_channels_only_for_the_transaction_company_and_never_without_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var companyAId = Guid.NewGuid();
        var companyBId = Guid.NewGuid();
        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            var group = new Group(Guid.NewGuid(), "Grupo Canais", actorId, requestId);
            var actor = new User(actorId, "Ator canais", "ator-canais@example.test");
            administrator.AddRange(
                actor,
                group,
                new Company(companyAId, group.Id, "Canais A", actorId, requestId),
                new Company(companyBId, group.Id, "Canais B", actorId, requestId)
            );
            await administrator.SaveChangesAsync();
        }

        // O trigger da migração semeia os canais por empresa nova.
        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            // set_config é local à transação: exige transação explícita.
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {companyAId.ToString()}, true)"
            );
            var visible = await runtime.Database.SqlQuery<string>(
                $"SELECT key AS \"Value\" FROM service_desk.atendimento_channels ORDER BY key"
            ).ToListAsync();
            Assert.Equal(["email", "manual", "web", "whatsapp"], visible);
            await transaction.RollbackAsync();
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM service_desk.atendimento_channels"
            ).ToListAsync();
            Assert.Empty(withoutContext);
        }
    }

    // ---------- Concurrency ----------

    [Fact]
    public async Task Stale_fields_update_conflicts_without_overwriting_current_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var winner = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: "critica", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        winner.EnsureSuccessStatusCode();

        using var stale = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: "baixa", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
        Assert.Equal("integrity_conflict", conflict?.Code);
        Assert.Equal("critica", conflict!.Current!.Priority);
    }

    [Fact]
    public async Task Repeated_assignment_enqueues_one_deterministic_notification_intent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);
        await SeedApprovedTemplateAsync(factory, scenario.CompanyId, "atendimento.card.assigned");

        using var first = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            new AssignCardRequest(scenario.CompanyId, Guid.NewGuid(), created.Version, scenario.AssigneeMembershipId)
        );
        first.EnsureSuccessStatusCode();
        var assigned = await first.Content.ReadFromJsonAsync<CardResponse>();

        // Repetição legítima do mesmo responsável em comando distinto: o intent
        // determinístico com chave leading (cartão + tipo) continua único.
        using var second = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/assign",
            new AssignCardRequest(
                scenario.CompanyId, Guid.NewGuid(), assigned!.Version, scenario.AssigneeMembershipId
            )
        );
        second.EnsureSuccessStatusCode();

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM notifications.notification_intents
            WHERE event_key = @event_key AND recipient_user_id = @recipient_user_id
            """,
            administrator
        );
        command.Parameters.AddWithValue(
            "event_key",
            $"atm.card.{created.Id:N}.assigned"
        );
        command.Parameters.AddWithValue("recipient_user_id", scenario.AssigneeUserId);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Queue_filters_status_priority_assignee_overdue_and_search_respond_server_side()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var urgent = await CreateCardAsync(client, scenario.CompanyId, "Instabilidade crítica");
        await CreateCardAsync(client, scenario.CompanyId, "Dúvida de faturamento");

        using var fields = await client.PostAsJsonAsync(
            $"/atendimento/cards/{urgent.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), urgent.Version,
                Priority: "alta", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        fields.EnsureSuccessStatusCode();

        using var byPriority = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&priority=alta"
        );
        byPriority.EnsureSuccessStatusCode();
        var prioritized = (await byPriority.Content.ReadFromJsonAsync<List<CardResponse>>())!;
        var highPriority = Assert.Single(prioritized);
        Assert.Equal(urgent.Id, highPriority.Id);

        using var bySearch = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&search={Uri.EscapeDataString("faturamento")}"
        );
        bySearch.EnsureSuccessStatusCode();
        var searched = (await bySearch.Content.ReadFromJsonAsync<List<CardResponse>>())!;
        Assert.Single(searched, card => card.Title == "Dúvida de faturamento");

        using var byAssignee = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&assigneeMembershipId={scenario.AssigneeMembershipId}"
        );
        byAssignee.EnsureSuccessStatusCode();
        Assert.Empty((await byAssignee.Content.ReadFromJsonAsync<List<CardResponse>>())!);
    }

    // ---------- Estado terminal ----------

    [Fact]
    public async Task Canceled_card_rejects_fields_update_and_never_reports_overdue()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                KnownCapabilities.ServiceDeskTicketCancel,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var scheduled = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: DateTimeOffset.UtcNow.AddHours(1),
                RequesterPersonRef: null, MentionedMembershipIds: null
            )
        );
        scheduled.EnsureSuccessStatusCode();
        var withSla = await scheduled.Content.ReadFromJsonAsync<CardResponse>();

        using var cancelled = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/cancel",
            new CancelCardRequest(
                scenario.CompanyId,
                Guid.NewGuid(),
                withSla!.Version,
                "Cliente desistiu."
            )
        );
        cancelled.EnsureSuccessStatusCode();
        var cancelledCard = await cancelled.Content.ReadFromJsonAsync<CardResponse>();
        Assert.Equal("cancelado", cancelledCard!.Column);

        // Cancelado é terminal: mesmo com a versão corrente correta, campos
        // nunca mudam (409 integrity_conflict com estado atual).
        using var denied = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), cancelledCard.Version,
                Priority: "critica", CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: "persona-x", MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var conflict = await denied.Content.ReadFromJsonAsync<IntegrityConflictResponse>();
        Assert.Equal("integrity_conflict", conflict?.Code);
        Assert.Equal("cancelado", conflict!.Current!.Column);
        Assert.Equal(cancelledCard.Version, conflict.Current.Version);

        // SLA vencido em cartão cancelado: fora do filtro overdue E com a
        // flag derivada falsa (a predica SQL e IsOverdue coincidem).
        await BackdateSlaAsync(factory, created.Id);
        using var overdueQueue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}&overdue=true"
        );
        overdueQueue.EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            (await overdueQueue.Content.ReadFromJsonAsync<List<CardResponse>>())!,
            card => card.Id == created.Id
        );

        using var queue = await client.GetAsync(
            $"/atendimento/cards?companyId={scenario.CompanyId}"
        );
        queue.EnsureSuccessStatusCode();
        var queued = Assert.Single((await queue.Content.ReadFromJsonAsync<List<CardResponse>>())!);
        Assert.Equal(created.Id, queued.Id);
        Assert.False(queued.IsOverdue);
    }

    // ---------- Idempotência de lookups ----------

    [Fact]
    public async Task Concurrent_identical_category_creates_reconcile_as_replay_not_conflict()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var elevated = await SeedScenarioAsync(
            factory,
            [KnownCapabilities.ServiceDeskTicketReadQueue, LookupAdminCapability],
            "Vinx Lookup Admin"
        );
        using var adminClient = factory.CreateClient();

        var requestId = Guid.NewGuid();
        var payload = new CategoryCreateRequest(elevated.CompanyId, requestId, "Concorrente");

        // Duas requests idênticas em paralelo: exatamente uma cria (201) e a
        // perdedora da corrida de nome único reconcilia como Replay (200).
        var first = adminClient.PostAsJsonAsync("/atendimento/categories", payload);
        var second = adminClient.PostAsJsonAsync("/atendimento/categories", payload);
        var responses = await Task.WhenAll(first, second);

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM service_desk.atendimento_categories WHERE company_id = @company_id AND name = 'Concorrente'",
            administrator);
        count.Parameters.AddWithValue("company_id", elevated.CompanyId);
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Category_request_id_reuse_conflicts_on_divergent_name_and_deactivation_replays()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var elevated = await SeedScenarioAsync(
            factory,
            [KnownCapabilities.ServiceDeskTicketReadQueue, LookupAdminCapability],
            "Vinx Lookup Admin"
        );
        using var adminClient = factory.CreateClient();

        // Reuso do mesmo requestId com nome divergente é conflito estável de
        // idempotência, não Replay da primeira categoria.
        var requestId = Guid.NewGuid();
        using var first = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, requestId, "Financeiro")
        );
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var category = await first.Content.ReadFromJsonAsync<CategoryResponse>();

        using var divergent = await adminClient.PostAsJsonAsync(
            "/atendimento/categories",
            new CategoryCreateRequest(elevated.CompanyId, requestId, "RH")
        );
        Assert.Equal(HttpStatusCode.Conflict, divergent.StatusCode);
        Assert.Equal(
            "idempotency_conflict",
            (await divergent.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        // Desativação: 200 Success com recibo; mesmo requestId repete 200.
        var deactivateRequestId = Guid.NewGuid();
        using var deactivated = await adminClient.PostAsJsonAsync(
            $"/atendimento/categories/{category!.Id}/deactivate",
            new CategoryDeactivateRequest(elevated.CompanyId, deactivateRequestId)
        );
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        var receipt = await deactivated.Content.ReadFromJsonAsync<CategoryResponse>();
        Assert.Equal(category.Id, receipt!.Id);

        using var replayed = await adminClient.PostAsJsonAsync(
            $"/atendimento/categories/{category.Id}/deactivate",
            new CategoryDeactivateRequest(elevated.CompanyId, deactivateRequestId)
        );
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        var replay = await replayed.Content.ReadFromJsonAsync<CategoryResponse>();
        Assert.Equal(receipt.Id, replay!.Id);

        // Comando novo em categoria já inativa é idempotente por natureza.
        using var freshCommand = await adminClient.PostAsJsonAsync(
            $"/atendimento/categories/{category.Id}/deactivate",
            new CategoryDeactivateRequest(elevated.CompanyId, Guid.NewGuid())
        );
        Assert.Equal(HttpStatusCode.OK, freshCommand.StatusCode);

        using var lookups = await adminClient.GetAsync(
            $"/atendimento/lookups?companyId={elevated.CompanyId}"
        );
        lookups.EnsureSuccessStatusCode();
        var lookupList = await lookups.Content.ReadFromJsonAsync<LookupsResponse>();
        Assert.Contains(
            lookupList!.Categories,
            candidate => candidate.Id == category.Id && !candidate.IsActive
        );
    }

    // ---------- Limites de entrada ----------

    [Fact]
    public async Task Fields_limits_reject_oversized_requester_excess_mentions_and_empty_guids()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
                FieldsCapability,
            ]
        );
        using var client = factory.CreateClient();
        var created = await CreateCardAsync(client, scenario.CompanyId);

        using var oversizedRequester = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: new string('p', 201),
                MentionedMembershipIds: null
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, oversizedRequester.StatusCode);

        var twentySixMentions = Enumerable.Range(0, 26)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        using var excessMentions = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null,
                MentionedMembershipIds: twentySixMentions
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, excessMentions.StatusCode);

        using var emptyGuidMention = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null,
                MentionedMembershipIds: [Guid.NewGuid(), Guid.Empty]
            )
        );
        Assert.Equal(HttpStatusCode.BadRequest, emptyGuidMention.StatusCode);

        // O teto exato de menções com referências válidas segue aceito.
        var validMentions = Enumerable.Range(0, 25)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        using var unknownMentions = await client.PostAsJsonAsync(
            $"/atendimento/cards/{created.Id}/fields",
            new UpdateFieldsRequest(
                scenario.CompanyId, Guid.NewGuid(), created.Version,
                Priority: null, CategoryRefId: null, ChannelRefId: null,
                SlaDueAt: null, RequesterPersonRef: null,
                MentionedMembershipIds: validMentions
            )
        );
        Assert.Equal(HttpStatusCode.NotFound, unknownMentions.StatusCode);
        Assert.Equal(
            "resource_not_found",
            (await unknownMentions.Content.ReadFromJsonAsync<ErrorResponse>())?.Code
        );

        using var queue = await client.GetAsync($"/atendimento/cards?companyId={scenario.CompanyId}");
        queue.EnsureSuccessStatusCode();
        var untouched = Assert.Single((await queue.Content.ReadFromJsonAsync<List<CardResponse>>())!);
        Assert.Equal(created.Version, untouched.Version);
        Assert.Null(untouched.RequesterPersonRef);
    }

    // ---------- Picker de pessoas ----------

    [Fact]
    public async Task Picker_options_need_directory_read_and_stay_company_scoped()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedScenarioAsync(
            factory,
            [
                KnownCapabilities.ServiceDeskTicketReadQueue,
                KnownCapabilities.ServiceDeskTicketCreate,
            ]
        );
        using var client = factory.CreateClient();

        // Sem directory.read o picker nega: ler opções é privilégio menor e
        // distinto de criar funcionário.
        using var denied = await client.GetAsync(
            $"/foundation/companies/{scenario.CompanyId}/directory-options"
        );
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var elevated = await SeedScenarioAsync(
            factory,
            [DirectoryReadCapability],
            "Vinx Directory Reader"
        );
        using var readerClient = factory.CreateClient();
        await SeedEmployeeAsync(
            factory,
            elevated.CompanyId,
            "Bea Picker",
            elevated.AssigneeMembershipId
        );
        await SeedEmployeeAsync(
            factory,
            elevated.CompanyId,
            "Caio Picker",
            elevated.OtherMembershipId
        );

        using var options = await readerClient.GetAsync(
            $"/foundation/companies/{elevated.CompanyId}/directory-options"
        );
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var people = await options.Content.ReadFromJsonAsync<List<PickerOptionResponse>>();
        Assert.NotNull(people);
        Assert.Equal(2, people!.Count);
        Assert.All(people, person => Assert.False(string.IsNullOrWhiteSpace(person.DisplayName)));
        Assert.Contains(people, person => person.DisplayName == "Bea Picker");

        // Empresa em que o ator é membro sem a capability nunca vaza opções.
        using var foreign = await readerClient.GetAsync(
            $"/foundation/companies/{scenario.CompanyId}/directory-options"
        );
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    // ---------- Infraestrutura compartilhada ----------


    private static async Task<CardResponse> CreateCardAsync(
        HttpClient client,
        Guid companyId,
        string title = "Acesso bloqueado"
    )
    {
        using var response = await client.PostAsJsonAsync(
            "/atendimento/cards",
            new CreateCardRequest(
                companyId,
                Guid.NewGuid(),
                title,
                "Cenário da fatia de usabilidade do atendimento."
            )
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardResponse>())!;
    }

    private static async Task<Guid> InsertCategoryRowAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string name
    )
    {
        var id = Guid.NewGuid();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_categories
                (id, company_id, name, created_by_user_id, updated_by_user_id, request_id)
            VALUES (@id, @company_id, @name, @actor, @actor, @request_id)
            """,
            administrator
        );
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("actor", factory.ActorUserId);
        command.Parameters.AddWithValue("request_id", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<Guid> LookupIdAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string tableName,
        string column,
        string value
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT id FROM {tableName} WHERE company_id = @company_id AND {column} = @value",
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("value", value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task BackdateSlaAsync(FoundationApiFactory factory, Guid cardId)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE service_desk.atendimento_cards SET sla_due_at = clock_timestamp() - interval '1 hour' WHERE id = @id",
            administrator
        );
        command.Parameters.AddWithValue("id", cardId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedEmployeeAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string displayName,
        Guid membershipId
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        administrator.Employees.Add(
            new Employee(Guid.NewGuid(), companyId, membershipId, displayName, factory.ActorUserId, Guid.NewGuid())
        );
        await administrator.SaveChangesAsync();
    }

    /// <summary>Aprova por empresa o template in-app exigido pelo enqueue.</summary>
    private static async Task SeedApprovedTemplateAsync(
        FoundationApiFactory factory,
        Guid companyId,
        string templateKey
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.notification_templates (company_id, channel, template_key, is_approved)
            VALUES (@company_id, 'in_app', @template_key, true)
            ON CONFLICT (company_id, channel, template_key) DO UPDATE SET is_approved = true
            """,
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        command.Parameters.AddWithValue("template_key", templateKey);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Cenário com papéis e capabilities concedidas por nome; capabilities
    /// ausentes do catálogo falham o cenário (RED esperado desta fatia).
    /// </summary>
    private static async Task<AtendimentoScenario> SeedScenarioAsync(
        FoundationApiFactory factory,
        string[]? grantedCapabilities = null,
        string? companyName = null
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var groupSuffix = Guid.NewGuid().ToString("N");
        var group = new Group(Guid.NewGuid(), $"Vinx Group {groupSuffix}", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            companyName is null ? $"Vinx Atendimento {groupSuffix}" : $"{companyName} {groupSuffix}",
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
        var assigneeUser = new User(Guid.NewGuid(), "Bea", $"bea-{Guid.NewGuid():N}@example.test");
        var assigneeMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            assigneeUser.Id,
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
            grantedCapabilities is { Length: > 0 }
                ? grantedCapabilities
                :
                [
                    KnownCapabilities.ServiceDeskTicketReadQueue,
                    KnownCapabilities.ServiceDeskTicketCreate,
                    KnownCapabilities.ServiceDeskTicketAssign,
                    KnownCapabilities.ServiceDeskTicketMove,
                    FieldsCapability,
                    "service_desk.ticket.watchers",
                ];
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
            assigneeUser,
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
        var otherUser = new User(Guid.NewGuid(), "Caio", $"caio-{Guid.NewGuid():N}@example.test");
        var otherMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            otherUser.Id,
            factory.ActorUserId,
            requestId
        );
        administrator.Add(otherUser);
        administrator.Add(otherMembership);
        await administrator.SaveChangesAsync();
        return new AtendimentoScenario(
            group.Id,
            company.Id,
            assigneeMembership.Id,
            assigneeUser.Id,
            otherMembership.Id
        );
    }

    private sealed record AtendimentoScenario(
        Guid GroupId,
        Guid CompanyId,
        Guid AssigneeMembershipId,
        Guid AssigneeUserId,
        Guid OtherMembershipId
    );

    private sealed record CreateCardRequest(
        Guid CompanyId,
        Guid RequestId,
        string Title,
        string Description
    );

    private sealed record AssignCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        Guid AssigneeMembershipId
    );

    private sealed record MoveCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string Column
    );

    private sealed record UpdateFieldsRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string? Priority,
        Guid? CategoryRefId,
        Guid? ChannelRefId,
        DateTimeOffset? SlaDueAt,
        string? RequesterPersonRef,
        IReadOnlyList<Guid>? MentionedMembershipIds
    );

    private sealed record WatcherRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        Guid MemberMembershipId
    );

    private sealed record CategoryCreateRequest(
        Guid CompanyId,
        Guid RequestId,
        string Name
    );

    private sealed record CategoryDeactivateRequest(Guid CompanyId, Guid RequestId);

    private sealed record CancelCardRequest(
        Guid CompanyId,
        Guid RequestId,
        long ExpectedVersion,
        string Reason
    );

    private sealed record CardResponse(
        Guid Id,
        Guid CompanyId,
        string Title,
        string Description,
        string Column,
        long Version,
        Guid? AssigneeMembershipId,
        string? Priority,
        Guid? CategoryRefId,
        Guid? ChannelRefId,
        DateTimeOffset? SlaDueAt,
        bool IsOverdue,
        string? RequesterPersonRef,
        IReadOnlyList<Guid>? WatcherMembershipIds
    );

    private sealed record TimelineEventResponse(
        Guid Id,
        string ActionKind,
        Guid? ActorMembershipId,
        string? ActorDisplayName,
        DateTimeOffset OccurredAt
    );

    private sealed record LookupCategoryDto(Guid Id, string Name, bool IsActive);

    private sealed record LookupChannelDto(Guid Id, string Key);

    private sealed record LookupsResponse(
        IReadOnlyList<LookupCategoryDto> Categories,
        IReadOnlyList<LookupChannelDto> Channels
    );

    private sealed record CategoryResponse(Guid Id, string Name);

    private sealed record PickerOptionResponse(Guid MembershipId, Guid EmployeeId, string DisplayName);

    private sealed record ErrorResponse(string Code);

    private sealed record IntegrityConflictResponse(string Code, CardResponse? Current);
}
