using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed record E2eFixtureProfile(
    string Name,
    Guid UserId,
    Guid ExternalIdentityId,
    string Subject,
    string DisplayName,
    string Email);

public sealed record E2eDatasetReceipt(
    Guid GroupId,
    Guid AlfaCompanyId,
    Guid BetaCompanyId,
    Guid GamaCompanyId);

public static class E2eFixtureProfileCatalog
{
    internal const string Issuer = "https://project.supabase.co/auth/v1";

    private static readonly E2eFixtureProfile Maria = new(
        "maria",
        Guid.Parse("b1000000-0000-4000-8000-000000000001"),
        Guid.Parse("b2000000-0000-4000-8000-000000000001"),
        "e2e-maria",
        "Maria Silva",
        "maria.silva@example.test");

    private static readonly E2eFixtureProfile Beatriz = new(
        "beatriz",
        Guid.Parse("b1000000-0000-4000-8000-000000000002"),
        Guid.Parse("b2000000-0000-4000-8000-000000000002"),
        "e2e-beatriz",
        "Beatriz Costa",
        "beatriz.costa@example.test");

    private static readonly E2eFixtureProfile Joao = new(
        "joao",
        Guid.Parse("b1000000-0000-4000-8000-000000000003"),
        Guid.Parse("b2000000-0000-4000-8000-000000000003"),
        "e2e-joao",
        "João Santos",
        "joao.santos@example.test");

    private static readonly E2eFixtureProfile Clara = new(
        "clara",
        Guid.Parse("b1000000-0000-4000-8000-000000000004"),
        Guid.Parse("b2000000-0000-4000-8000-000000000004"),
        "e2e-clara",
        "Clara Oliveira",
        "clara.oliveira@example.test");

    public static bool TryGet(string name, out E2eFixtureProfile? profile)
    {
        profile = name switch
        {
            "maria" => Maria,
            "beatriz" => Beatriz,
            "joao" => Joao,
            "clara" => Clara,
            _ => null
        };
        return profile is not null;
    }
}

public static class E2eFixtureSeeder
{
    private static readonly JsonSerializerOptions SeedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GroupId = FixtureId("a1000000", 1);
    private static readonly Guid AlfaCompanyId = FixtureId("a2000000", 1);
    private static readonly Guid BetaCompanyId = FixtureId("a2000000", 2);
    private static readonly Guid GamaCompanyId = FixtureId("a2000000", 3);
    private static readonly Guid MariaAlfaMembershipId = FixtureId("b3000000", 1);
    private static readonly Guid MariaBetaMembershipId = FixtureId("b3000000", 2);
    private static readonly Guid BeatrizAlfaMembershipId = FixtureId("b3000000", 3);
    private static readonly Guid JoaoBetaMembershipId = FixtureId("b3000000", 4);
    private static readonly Guid ClaraGamaMembershipId = FixtureId("b3000000", 5);
    private static readonly Guid AlfaOperationsRoleId = FixtureId("d1000000", 1);
    private static readonly Guid AlfaAtendimentoRoleId = FixtureId("d1000000", 2);
    private static readonly Guid BetaReaderRoleId = FixtureId("d1000000", 3);
    private static readonly Guid BetaFinanceRoleId = FixtureId("d1000000", 4);
    private static readonly Guid GamaRestrictedRoleId = FixtureId("d1000000", 5);
    private static readonly E2eDatasetReceipt ExpectedReceipt = new(
        GroupId,
        AlfaCompanyId,
        BetaCompanyId,
        GamaCompanyId);

    /// <summary>
    /// Semeia o dataset completo em uma transação e recusa banco já populado.
    /// </summary>
    public static async Task<E2eDatasetReceipt> SeedAsync(
        FoundationPostgresFixture fixture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = GetProfiles();
        var seedJson = CreateSeedJson(profiles);

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await DatabaseIsEmptyAsync(connection, transaction, cancellationToken))
        {
            throw new InvalidOperationException("e2e fixture database is not empty");
        }

        if (!await CapabilityCatalogIsCompleteAsync(
                connection,
                transaction,
                seedJson,
                cancellationToken))
        {
            throw new InvalidOperationException("e2e fixture seed catalog is incomplete");
        }

        await ExecuteSeedAsync(connection, transaction, seedJson, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ExpectedReceipt;
    }

    /// <summary>
    /// Confirma IDs, isolamento multiempresa, grants e linhas de módulos do recibo.
    /// </summary>
    public static async Task VerifyAsync(
        FoundationPostgresFixture fixture,
        E2eDatasetReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt != ExpectedReceipt)
        {
            throw new InvalidOperationException("e2e fixture verification failed");
        }

        var profiles = GetProfiles();
        var seedJson = CreateSeedJson(profiles);
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(VerificationSql, connection);
        AddParameters(
            command,
            ("issuer", E2eFixtureProfileCatalog.Issuer),
            ("seed", seedJson));
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("e2e fixture verification failed");
        }
    }

    private static async Task ExecuteSeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string seedJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SeedSql, connection, transaction);
        AddParameters(
            command,
            ("seed", seedJson),
            ("issuer", E2eFixtureProfileCatalog.Issuer),
            ("at", FixtureTimestamp),
            ("claimed_at", FixtureTimestamp.AddMinutes(1)),
            ("resolved_at", FixtureTimestamp.AddMinutes(2)),
            ("read_at", FixtureTimestamp.AddMinutes(3)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> DatabaseIsEmptyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT NOT EXISTS (SELECT 1 FROM foundation.users)",
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<bool> CapabilityCatalogIsCompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string seedJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH expected AS (
                SELECT DISTINCT capability_name
                FROM jsonb_to_recordset(@seed::jsonb -> 'grants')
                    AS seed(capability_name text))
            SELECT count(capability.id) = count(expected.capability_name)
            FROM expected
            LEFT JOIN foundation.capabilities AS capability
                ON capability.name = expected.capability_name
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("seed", seedJson);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static string CreateSeedJson(FixtureProfiles profile) =>
        JsonSerializer.Serialize(
            new
            {
                Users = new[]
                {
                    new { profile.Maria.UserId, profile.Maria.DisplayName, profile.Maria.Email },
                    new { profile.Beatriz.UserId, profile.Beatriz.DisplayName, profile.Beatriz.Email },
                    new { profile.Joao.UserId, profile.Joao.DisplayName, profile.Joao.Email },
                    new { profile.Clara.UserId, profile.Clara.DisplayName, profile.Clara.Email }
                },
                ExternalIdentities = new[]
                {
                    new { profile.Maria.ExternalIdentityId, profile.Maria.UserId, profile.Maria.Subject },
                    new { profile.Beatriz.ExternalIdentityId, profile.Beatriz.UserId, profile.Beatriz.Subject },
                    new { profile.Joao.ExternalIdentityId, profile.Joao.UserId, profile.Joao.Subject },
                    new { profile.Clara.ExternalIdentityId, profile.Clara.UserId, profile.Clara.Subject }
                },
                Groups = new[]
                {
                    new { Id = GroupId, Name = "Vinx Demo Group", ActorUserId = profile.Maria.UserId, RequestId = RequestId(1) }
                },
                Companies = new[]
                {
                    new { Id = AlfaCompanyId, GroupId, Name = "Alfa", ActorUserId = profile.Maria.UserId, RequestId = RequestId(2) },
                    new { Id = BetaCompanyId, GroupId, Name = "Beta", ActorUserId = profile.Joao.UserId, RequestId = RequestId(3) },
                    new { Id = GamaCompanyId, GroupId, Name = "Gama", ActorUserId = profile.Clara.UserId, RequestId = RequestId(4) }
                },
                Memberships = new[]
                {
                    new { Id = MariaAlfaMembershipId, CompanyId = AlfaCompanyId, profile.Maria.UserId, ActorUserId = profile.Maria.UserId, RequestId = RequestId(5) },
                    new { Id = MariaBetaMembershipId, CompanyId = BetaCompanyId, profile.Maria.UserId, ActorUserId = profile.Maria.UserId, RequestId = RequestId(6) },
                    new { Id = BeatrizAlfaMembershipId, CompanyId = AlfaCompanyId, profile.Beatriz.UserId, ActorUserId = profile.Beatriz.UserId, RequestId = RequestId(7) },
                    new { Id = JoaoBetaMembershipId, CompanyId = BetaCompanyId, profile.Joao.UserId, ActorUserId = profile.Joao.UserId, RequestId = RequestId(8) },
                    new { Id = ClaraGamaMembershipId, CompanyId = GamaCompanyId, profile.Clara.UserId, ActorUserId = profile.Clara.UserId, RequestId = RequestId(9) }
                },
                Units = new[]
                {
                    new { Id = FixtureId("c1000000", 1), CompanyId = AlfaCompanyId, Name = "Operações", ActorUserId = profile.Maria.UserId, RequestId = RequestId(10) },
                    new { Id = FixtureId("c1000000", 2), CompanyId = BetaCompanyId, Name = "Financeiro", ActorUserId = profile.Joao.UserId, RequestId = RequestId(11) },
                    new { Id = FixtureId("c1000000", 3), CompanyId = GamaCompanyId, Name = "Restrita", ActorUserId = profile.Clara.UserId, RequestId = RequestId(12) }
                },
                Teams = new[]
                {
                    new { Id = FixtureId("c2000000", 1), CompanyId = AlfaCompanyId, UnitId = FixtureId("c1000000", 1), Name = "Atendimento", ActorUserId = profile.Maria.UserId, RequestId = RequestId(13) },
                    new { Id = FixtureId("c2000000", 2), CompanyId = BetaCompanyId, UnitId = FixtureId("c1000000", 2), Name = "Contas", ActorUserId = profile.Joao.UserId, RequestId = RequestId(14) },
                    new { Id = FixtureId("c2000000", 3), CompanyId = GamaCompanyId, UnitId = FixtureId("c1000000", 3), Name = "Restrita", ActorUserId = profile.Clara.UserId, RequestId = RequestId(15) }
                },
                Roles = new[]
                {
                    new { Id = AlfaOperationsRoleId, CompanyId = AlfaCompanyId, Name = "Operations", ActorUserId = profile.Maria.UserId, RequestId = RequestId(16) },
                    new { Id = AlfaAtendimentoRoleId, CompanyId = AlfaCompanyId, Name = "Atendimento agent", ActorUserId = profile.Beatriz.UserId, RequestId = RequestId(17) },
                    new { Id = BetaReaderRoleId, CompanyId = BetaCompanyId, Name = "Reader", ActorUserId = profile.Maria.UserId, RequestId = RequestId(18) },
                    new { Id = BetaFinanceRoleId, CompanyId = BetaCompanyId, Name = "Finance operator", ActorUserId = profile.Joao.UserId, RequestId = RequestId(19) },
                    new { Id = GamaRestrictedRoleId, CompanyId = GamaCompanyId, Name = "Restricted", ActorUserId = profile.Clara.UserId, RequestId = RequestId(20) }
                },
                Assignments = new[]
                {
                    new { Id = FixtureId("d2000000", 1), CompanyId = AlfaCompanyId, RoleId = AlfaOperationsRoleId, MembershipId = MariaAlfaMembershipId, ActorUserId = profile.Maria.UserId, RequestId = RequestId(21) },
                    new { Id = FixtureId("d2000000", 2), CompanyId = AlfaCompanyId, RoleId = AlfaAtendimentoRoleId, MembershipId = BeatrizAlfaMembershipId, ActorUserId = profile.Beatriz.UserId, RequestId = RequestId(22) },
                    new { Id = FixtureId("d2000000", 3), CompanyId = BetaCompanyId, RoleId = BetaReaderRoleId, MembershipId = MariaBetaMembershipId, ActorUserId = profile.Maria.UserId, RequestId = RequestId(23) },
                    new { Id = FixtureId("d2000000", 4), CompanyId = BetaCompanyId, RoleId = BetaFinanceRoleId, MembershipId = JoaoBetaMembershipId, ActorUserId = profile.Joao.UserId, RequestId = RequestId(24) },
                    new { Id = FixtureId("d2000000", 5), CompanyId = GamaCompanyId, RoleId = GamaRestrictedRoleId, MembershipId = ClaraGamaMembershipId, ActorUserId = profile.Clara.UserId, RequestId = RequestId(25) }
                },
                Grants = CreateGrantRows(profile),
                Employees = new[]
                {
                    new { Id = FixtureId("e1000000", 1), CompanyId = AlfaCompanyId, MembershipId = MariaAlfaMembershipId, profile.Maria.DisplayName, ActorUserId = profile.Maria.UserId, RequestId = RequestId(43) },
                    new { Id = FixtureId("e1000000", 2), CompanyId = AlfaCompanyId, MembershipId = BeatrizAlfaMembershipId, profile.Beatriz.DisplayName, ActorUserId = profile.Beatriz.UserId, RequestId = RequestId(44) },
                    new { Id = FixtureId("e1000000", 3), CompanyId = BetaCompanyId, MembershipId = MariaBetaMembershipId, profile.Maria.DisplayName, ActorUserId = profile.Maria.UserId, RequestId = RequestId(45) },
                    new { Id = FixtureId("e1000000", 4), CompanyId = BetaCompanyId, MembershipId = JoaoBetaMembershipId, profile.Joao.DisplayName, ActorUserId = profile.Joao.UserId, RequestId = RequestId(46) },
                    new { Id = FixtureId("e1000000", 5), CompanyId = GamaCompanyId, MembershipId = ClaraGamaMembershipId, profile.Clara.DisplayName, ActorUserId = profile.Clara.UserId, RequestId = RequestId(47) }
                },
                Paguai = new[]
                {
                    new { Id = FixtureId("f1000000", 1), CompanyId = AlfaCompanyId, RequesterUserId = profile.Maria.UserId, RequestId = RequestId(48), Title = "Licença de software", Description = "Renovação anual das licenças da equipe de operações.", Amount = 1299.90m, DueDate = new DateOnly(2026, 8, 15), BeneficiaryDisplayName = "SaaS Brasil Ltda.", ProofRequirement = "required" },
                    new { Id = FixtureId("f1000000", 2), CompanyId = AlfaCompanyId, RequesterUserId = profile.Beatriz.UserId, RequestId = RequestId(49), Title = "Suprimentos do escritório", Description = "Reposição mensal de café e materiais de apoio.", Amount = 480.50m, DueDate = new DateOnly(2026, 8, 20), BeneficiaryDisplayName = "Comercial Alfa Ltda.", ProofRequirement = "optional" },
                    new { Id = FixtureId("f1000000", 3), CompanyId = BetaCompanyId, RequesterUserId = profile.Maria.UserId, RequestId = RequestId(50), Title = "Serviços contábeis", Description = "Honorários contábeis referentes ao mês de agosto.", Amount = 8500.00m, DueDate = new DateOnly(2026, 8, 10), BeneficiaryDisplayName = "Contabilidade Central Ltda.", ProofRequirement = "required" },
                    new { Id = FixtureId("f1000000", 4), CompanyId = BetaCompanyId, RequesterUserId = profile.Joao.UserId, RequestId = RequestId(51), Title = "Hospedagem em nuvem", Description = "Infraestrutura dos ambientes internos da empresa Beta.", Amount = 2340.75m, DueDate = new DateOnly(2026, 8, 25), BeneficiaryDisplayName = "Nuvem Serviços S.A.", ProofRequirement = "required" }
                },
                Cards = new[]
                {
                    new { Id = FixtureId("f2000000", 1), CompanyId = AlfaCompanyId, Title = "Configurar novo cliente", Description = "Preparar o cadastro e a primeira orientação do cliente.", ColumnName = "novo", AssigneeMembershipId = (Guid?)null, CreatedByUserId = profile.Maria.UserId, UpdatedByUserId = profile.Maria.UserId, RequestId = RequestId(52) },
                    new { Id = FixtureId("f2000000", 2), CompanyId = AlfaCompanyId, Title = "Revisar integração", Description = "Validar o retorno da integração antes da entrega.", ColumnName = "em_andamento", AssigneeMembershipId = (Guid?)BeatrizAlfaMembershipId, CreatedByUserId = profile.Maria.UserId, UpdatedByUserId = profile.Beatriz.UserId, RequestId = RequestId(53) },
                    new { Id = FixtureId("f2000000", 3), CompanyId = AlfaCompanyId, Title = "Atendimento concluído", Description = "Solicitação resolvida e confirmada com a equipe solicitante.", ColumnName = "concluido", AssigneeMembershipId = (Guid?)null, CreatedByUserId = profile.Beatriz.UserId, UpdatedByUserId = profile.Beatriz.UserId, RequestId = RequestId(54) },
                    new { Id = FixtureId("f2000000", 4), CompanyId = BetaCompanyId, Title = "Conferir cobrança", Description = "Analisar a divergência informada pelo financeiro.", ColumnName = "novo", AssigneeMembershipId = (Guid?)null, CreatedByUserId = profile.Joao.UserId, UpdatedByUserId = profile.Joao.UserId, RequestId = RequestId(55) },
                    new { Id = FixtureId("f2000000", 5), CompanyId = GamaCompanyId, Title = "Caso restrito", Description = "Registro isolado disponível somente para a empresa Gama.", ColumnName = "em_andamento", AssigneeMembershipId = (Guid?)ClaraGamaMembershipId, CreatedByUserId = profile.Clara.UserId, UpdatedByUserId = profile.Clara.UserId, RequestId = RequestId(56) }
                },
                Notifications = new[]
                {
                    new { Id = FixtureId("f3000000", 1), CompanyId = AlfaCompanyId, RecipientUserId = profile.Maria.UserId, EventKey = "e2e.paguai.created.alfa", DeepLinkSelector = "/paguai", CorrelationId = RequestId(57), CreatedByUserId = profile.Maria.UserId, ProviderAttemptId = RequestId(61), ReadByUserId = (Guid?)profile.Maria.UserId },
                    new { Id = FixtureId("f3000000", 2), CompanyId = AlfaCompanyId, RecipientUserId = profile.Maria.UserId, EventKey = "e2e.atendimento.assigned.alfa", DeepLinkSelector = "/atendimento", CorrelationId = RequestId(58), CreatedByUserId = profile.Maria.UserId, ProviderAttemptId = RequestId(62), ReadByUserId = (Guid?)null },
                    new { Id = FixtureId("f3000000", 3), CompanyId = BetaCompanyId, RecipientUserId = profile.Maria.UserId, EventKey = "e2e.paguai.review.beta", DeepLinkSelector = "/paguai", CorrelationId = RequestId(59), CreatedByUserId = profile.Maria.UserId, ProviderAttemptId = RequestId(63), ReadByUserId = (Guid?)null },
                    new { Id = FixtureId("f3000000", 4), CompanyId = GamaCompanyId, RecipientUserId = profile.Clara.UserId, EventKey = "e2e.atendimento.restricted.gama", DeepLinkSelector = "/atendimento", CorrelationId = RequestId(60), CreatedByUserId = profile.Clara.UserId, ProviderAttemptId = RequestId(64), ReadByUserId = (Guid?)null }
                }
            },
            SeedJsonOptions);

    private static GrantSeedRow[] CreateGrantRows(FixtureProfiles profile)
    {
        var rows = new List<GrantSeedRow>();
        AddGrants(rows, AlfaCompanyId, AlfaOperationsRoleId, profile.Maria.UserId, 1, 26,
            "paguai.draft.create", "paguai.draft.read", "service_desk.ticket.read_queue",
            "service_desk.ticket.create", "service_desk.ticket.assign",
            "service_desk.ticket.move", "foundation.employees.create");
        AddGrants(rows, AlfaCompanyId, AlfaAtendimentoRoleId, profile.Beatriz.UserId, 8, 33,
            "service_desk.ticket.read_queue", "service_desk.ticket.create",
            "service_desk.ticket.assign", "service_desk.ticket.move",
            "foundation.employees.create");
        AddGrants(rows, BetaCompanyId, BetaReaderRoleId, profile.Maria.UserId, 13, 38,
            "paguai.draft.read", "service_desk.ticket.read_queue");
        AddGrants(rows, BetaCompanyId, BetaFinanceRoleId, profile.Joao.UserId, 15, 40,
            "paguai.draft.create", "paguai.draft.read");
        AddGrants(rows, GamaCompanyId, GamaRestrictedRoleId, profile.Clara.UserId, 17, 42,
            "service_desk.ticket.read_queue");
        AddGrants(rows, AlfaCompanyId, AlfaOperationsRoleId, profile.Maria.UserId, 18, 43,
            "ai.read");
        return rows.ToArray();
    }

    private static void AddGrants(
        ICollection<GrantSeedRow> rows,
        Guid companyId,
        Guid roleId,
        Guid actorUserId,
        int firstGrant,
        int firstRequest,
        params string[] capabilityNames)
    {
        for (var offset = 0; offset < capabilityNames.Length; offset++)
        {
            rows.Add(new GrantSeedRow(
                FixtureId("d3000000", firstGrant + offset),
                companyId,
                roleId,
                capabilityNames[offset],
                actorUserId,
                RequestId(firstRequest + offset)));
        }
    }

    private static FixtureProfiles GetProfiles() => new(
        GetRequiredProfile("maria"),
        GetRequiredProfile("beatriz"),
        GetRequiredProfile("joao"),
        GetRequiredProfile("clara"));

    private static E2eFixtureProfile GetRequiredProfile(string name)
    {
        if (E2eFixtureProfileCatalog.TryGet(name, out var profile))
        {
            return profile!;
        }

        throw new InvalidOperationException("e2e fixture profile catalog is incomplete");
    }

    private static void AddParameters(
        NpgsqlCommand command,
        params (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static Guid RequestId(int sequence) => FixtureId("f4000000", sequence);

    private static Guid FixtureId(string family, int sequence)
    {
        var suffix = sequence.ToString("000000000000", CultureInfo.InvariantCulture);
        return Guid.ParseExact($"{family}-0000-4000-8000-{suffix}", "D");
    }

    private sealed record FixtureProfiles(
        E2eFixtureProfile Maria,
        E2eFixtureProfile Beatriz,
        E2eFixtureProfile Joao,
        E2eFixtureProfile Clara);

    private sealed record GrantSeedRow(
        Guid Id,
        Guid CompanyId,
        Guid RoleId,
        string CapabilityName,
        Guid ActorUserId,
        Guid RequestId);

    private const string SeedSql =
        """
        INSERT INTO foundation.users (id, display_name, email, created_at)
        SELECT user_id, display_name, email, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'users')
            AS seed(user_id uuid, display_name text, email text);

        INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
        SELECT external_identity_id, user_id, @issuer, subject, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'external_identities')
            AS seed(external_identity_id uuid, user_id uuid, subject text);

        INSERT INTO foundation.groups
            (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, name, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'groups')
            AS seed(id uuid, name text, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.companies
            (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, group_id, name, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'companies')
            AS seed(id uuid, group_id uuid, name text, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.memberships
            (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, company_id, user_id, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'memberships')
            AS seed(id uuid, company_id uuid, user_id uuid, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.units
            (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, company_id, name, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'units')
            AS seed(id uuid, company_id uuid, name text, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.teams
            (id, company_id, unit_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, company_id, unit_id, name, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'teams')
            AS seed(id uuid, company_id uuid, unit_id uuid, name text, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.roles
            (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, company_id, name, actor_user_id, actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'roles')
            AS seed(id uuid, company_id uuid, name text, actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.role_assignments
            (id, company_id, role_id, membership_id, created_by_user_id, updated_by_user_id,
                request_id, created_at, updated_at)
        SELECT id, company_id, role_id, membership_id, actor_user_id, actor_user_id,
            request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'assignments')
            AS seed(id uuid, company_id uuid, role_id uuid, membership_id uuid,
                actor_user_id uuid, request_id uuid);

        INSERT INTO foundation.role_capabilities
            (id, company_id, role_id, capability_id, scope, created_by_user_id,
                updated_by_user_id, request_id, created_at, updated_at)
        SELECT seed.id, seed.company_id, seed.role_id, capability.id, 'company',
            seed.actor_user_id, seed.actor_user_id, seed.request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'grants')
            AS seed(id uuid, company_id uuid, role_id uuid, capability_name text,
                actor_user_id uuid, request_id uuid)
        JOIN foundation.capabilities AS capability ON capability.name = seed.capability_name;

        INSERT INTO foundation.employees
            (id, company_id, membership_id, display_name, created_by_user_id,
                updated_by_user_id, request_id, created_at, updated_at)
        SELECT id, company_id, membership_id, display_name, actor_user_id,
            actor_user_id, request_id, @at, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'employees')
            AS seed(id uuid, company_id uuid, membership_id uuid, display_name text,
                actor_user_id uuid, request_id uuid);

        INSERT INTO paguai.requests
            (id, company_id, requester_user_id, request_id, title, description,
                amount, currency, due_date, beneficiary_display_name, proof_requirement,
                state, version, created_at)
        SELECT id, company_id, requester_user_id, request_id, title, description,
            amount, 'BRL', due_date, beneficiary_display_name, proof_requirement,
            'rascunho', 1, @at
        FROM jsonb_to_recordset(@seed::jsonb -> 'paguai')
            AS seed(id uuid, company_id uuid, requester_user_id uuid, request_id uuid,
                title text, description text, amount numeric(18,2), due_date date,
                beneficiary_display_name text, proof_requirement text);

        INSERT INTO service_desk.atendimento_cards
            (id, company_id, title, description, column_name, assignee_membership_id,
                created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
        SELECT id, company_id, title, description, column_name, assignee_membership_id,
            created_by_user_id, updated_by_user_id, request_id, @at, @at, 1
        FROM jsonb_to_recordset(@seed::jsonb -> 'cards')
            AS seed(id uuid, company_id uuid, title text, description text, column_name text,
                assignee_membership_id uuid, created_by_user_id uuid, updated_by_user_id uuid,
                request_id uuid);

        INSERT INTO notifications.notification_intents
            (id, company_id, recipient_user_id, event_key, channel, template_key,
                deep_link_selector, status, attempt_count, correlation_id, created_by_user_id,
                created_at, updated_at, provider_attempt_id, provider_claimed_at,
                provider_resolved_at, failure_reason, read_at, read_by_user_id)
        SELECT id, company_id, recipient_user_id, event_key, 'in_app', 'e2e.fixture',
            deep_link_selector, 'delivered', 1, correlation_id, created_by_user_id,
            @at, @resolved_at, provider_attempt_id, @claimed_at, @resolved_at, NULL,
            CASE WHEN read_by_user_id IS NULL THEN NULL ELSE @read_at END, read_by_user_id
        FROM jsonb_to_recordset(@seed::jsonb -> 'notifications')
            AS seed(id uuid, company_id uuid, recipient_user_id uuid, event_key text,
                deep_link_selector text, correlation_id uuid, created_by_user_id uuid,
                provider_attempt_id uuid, read_by_user_id uuid);
        """;

    private const string VerificationSql =
        """
        SELECT
            (SELECT jsonb_agg(jsonb_build_object(
                    'user_id', id, 'display_name', display_name, 'email', email) ORDER BY id)
                FROM foundation.users) = (@seed::jsonb -> 'users')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'external_identity_id', id, 'user_id', user_id, 'subject', subject) ORDER BY id)
                FROM foundation.external_identities) = (@seed::jsonb -> 'external_identities')
            AND NOT EXISTS (SELECT 1 FROM foundation.external_identities WHERE issuer <> @issuer)
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'name', name, 'actor_user_id', created_by_user_id,
                    'request_id', request_id) ORDER BY id)
                FROM foundation.groups) = (@seed::jsonb -> 'groups')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'group_id', group_id, 'name', name,
                    'actor_user_id', created_by_user_id, 'request_id', request_id) ORDER BY id)
                FROM foundation.companies) = (@seed::jsonb -> 'companies')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'user_id', user_id,
                    'actor_user_id', created_by_user_id, 'request_id', request_id) ORDER BY id)
                FROM foundation.memberships) = (@seed::jsonb -> 'memberships')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'name', name,
                    'actor_user_id', created_by_user_id, 'request_id', request_id) ORDER BY id)
                FROM foundation.units) = (@seed::jsonb -> 'units')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'unit_id', unit_id, 'name', name,
                    'actor_user_id', created_by_user_id, 'request_id', request_id) ORDER BY id)
                FROM foundation.teams) = (@seed::jsonb -> 'teams')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'name', name,
                    'actor_user_id', created_by_user_id, 'request_id', request_id) ORDER BY id)
                FROM foundation.roles) = (@seed::jsonb -> 'roles')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'role_id', role_id,
                    'membership_id', membership_id, 'actor_user_id', created_by_user_id,
                    'request_id', request_id) ORDER BY id)
                FROM foundation.role_assignments) = (@seed::jsonb -> 'assignments')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', grant_row.id, 'company_id', grant_row.company_id,
                    'role_id', grant_row.role_id, 'capability_name', capability.name,
                    'actor_user_id', grant_row.created_by_user_id,
                    'request_id', grant_row.request_id) ORDER BY grant_row.id)
                FROM foundation.role_capabilities AS grant_row
                JOIN foundation.capabilities AS capability ON capability.id = grant_row.capability_id)
                    = (@seed::jsonb -> 'grants')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'membership_id', membership_id,
                    'display_name', display_name, 'actor_user_id', created_by_user_id,
                    'request_id', request_id) ORDER BY id)
                FROM foundation.employees) = (@seed::jsonb -> 'employees')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'requester_user_id', requester_user_id,
                    'request_id', request_id, 'title', title, 'description', description,
                    'amount', amount, 'due_date', due_date,
                    'beneficiary_display_name', beneficiary_display_name,
                    'proof_requirement', proof_requirement) ORDER BY id)
                FROM paguai.requests) = (@seed::jsonb -> 'paguai')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'title', title,
                    'description', description, 'column_name', column_name,
                    'assignee_membership_id', assignee_membership_id,
                    'created_by_user_id', created_by_user_id,
                    'updated_by_user_id', updated_by_user_id,
                    'request_id', request_id) ORDER BY id)
                FROM service_desk.atendimento_cards) = (@seed::jsonb -> 'cards')
            AND (SELECT jsonb_agg(jsonb_build_object(
                    'id', id, 'company_id', company_id, 'recipient_user_id', recipient_user_id,
                    'event_key', event_key, 'deep_link_selector', deep_link_selector,
                    'correlation_id', correlation_id, 'created_by_user_id', created_by_user_id,
                    'provider_attempt_id', provider_attempt_id,
                    'read_by_user_id', read_by_user_id) ORDER BY id)
                FROM notifications.notification_intents) = (@seed::jsonb -> 'notifications')
            AND NOT EXISTS (
                SELECT 1 FROM (
                    SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.groups
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.companies
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.memberships
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.units
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.teams
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.roles
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.role_assignments
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.role_capabilities
                    UNION ALL SELECT created_by_user_id, updated_by_user_id, deleted_at,
                        deleted_by_user_id, deleted_reason FROM foundation.employees) AS lifecycle
                WHERE created_by_user_id IS DISTINCT FROM updated_by_user_id
                    OR deleted_at IS NOT NULL OR deleted_by_user_id IS NOT NULL
                    OR deleted_reason IS NOT NULL)
            AND NOT EXISTS (SELECT 1 FROM foundation.role_capabilities WHERE scope <> 'company')
            AND NOT EXISTS (SELECT 1 FROM paguai.requests
                WHERE state <> 'rascunho' OR version <> 1 OR currency <> 'BRL'
                    OR source_inbound_message_id IS NOT NULL)
            AND NOT EXISTS (SELECT 1 FROM service_desk.atendimento_cards
                WHERE row_version <> 1 OR deleted_at IS NOT NULL
                    OR deleted_by_user_id IS NOT NULL OR deleted_reason IS NOT NULL)
            AND NOT EXISTS (SELECT 1 FROM notifications.notification_intents
                WHERE channel <> 'in_app' OR template_key <> 'e2e.fixture'
                    OR status <> 'delivered' OR attempt_count <> 1
                    OR provider_attempt_id IS NULL OR provider_claimed_at IS NULL
                    OR provider_resolved_at IS NULL OR failure_reason IS NOT NULL
                    OR ((read_at IS NULL) <> (read_by_user_id IS NULL))
                    OR (read_by_user_id IS NOT NULL AND read_by_user_id <> recipient_user_id))
        """;
}
