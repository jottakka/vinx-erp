using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Permissions;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Contrato da leitura do diretório de permissões: gate de capability na função
/// SQL (403 sem oráculo de existência), ordering estável e email nunca exposto.
/// </summary>
public sealed class CompanyMemberPermissionsReadEndpointTests
{
    private const string RolesAssignCapability = "foundation.roles.assign";
    private const string FilesUploadCapability = "foundation.files.upload";
    private const string FilesConfirmCapability = "foundation.files.confirm";
    private const string MembersListAction = "foundation.permissions.members.list";
    private const string RolesListAction = "foundation.permissions.roles.list";

    [Fact]
    public async Task List_returns_members_with_roles_and_capabilities()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{scenario.CompanyId}/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        var members = root.EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        Assert.Equal(
            new List<string?> { "Ana", "Bruno", "Carla" },
            members.Select(member => member.GetProperty("displayName").GetString()).ToList()
        );

        var ana = members[0];
        Assert.Equal(scenario.ActorMembershipId, ana.GetProperty("membershipId").GetGuid());
        var adminRole = ana.GetProperty("roles").EnumerateArray().Single();
        Assert.Equal(scenario.AdminRoleId, adminRole.GetProperty("roleId").GetGuid());
        Assert.Equal("Admin", adminRole.GetProperty("name").GetString());
        Assert.Equal(
            new List<string?> { FilesUploadCapability, RolesAssignCapability },
            ana.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToList()
        );
        Assert.Equal(JsonValueKind.Null, ana.GetProperty("invitationStatus").ValueKind);

        var bruno = members[1];
        var viewerRole = bruno.GetProperty("roles").EnumerateArray().Single();
        Assert.Equal("Viewer", viewerRole.GetProperty("name").GetString());
        Assert.Empty(
            bruno.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString())
        );

        // Membro sem papel continua listado, com coleções vazias.
        var carla = members[2];
        Assert.Empty(carla.GetProperty("roles").EnumerateArray());
        Assert.Empty(carla.GetProperty("capabilities").EnumerateArray());
    }

    [Fact]
    public async Task List_returns_pending_acceptance_invitation_status()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        // Estado pós-entrega confirmada pelo provedor: o convite persiste até o
        // primeiro login do convidado (aceitação é fatia posterior).
        await using var connection = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.pending_member_invitations (
                id, membership_id, user_id, company_id, status, created_at,
                provider_pending_at, provider_attempt_id, provider_started_at,
                provider_completed_at, provider_issuer, provider_subject)
            VALUES (
                @invitation_id, @membership_id, @user_id, @company_id,
                'pending_acceptance', clock_timestamp(),
                clock_timestamp(), @provider_attempt_id, clock_timestamp(),
                clock_timestamp(), 'https://provider.example/auth/v1', @provider_subject)
            """,
            connection
        );
        command.Parameters.AddWithValue("invitation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("membership_id", scenario.BrunoMembershipId);
        command.Parameters.AddWithValue("user_id", scenario.BrunoUserId);
        command.Parameters.AddWithValue("company_id", scenario.CompanyId);
        command.Parameters.AddWithValue("provider_attempt_id", Guid.NewGuid());
        command.Parameters.AddWithValue("provider_subject", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{scenario.CompanyId}/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var members = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, members.Count);
        var bruno = members.Single(member =>
            member.GetProperty("displayName").GetString() == "Bruno"
        );
        Assert.Equal(
            "pending_acceptance",
            bruno.GetProperty("invitationStatus").GetString()
        );
        var ana = members.Single(member =>
            member.GetProperty("displayName").GetString() == "Ana"
        );
        Assert.Equal(JsonValueKind.Null, ana.GetProperty("invitationStatus").ValueKind);
    }

    [Fact]
    public async Task Member_payloads_never_expose_email()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{scenario.CompanyId}/members");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requires_authentication_membership_and_capability()
    {
        await using var anonymous = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var unauthenticatedMembers = await anonymous
            .CreateClient()
            .GetAsync($"/foundation/companies/{Guid.NewGuid()}/members");
        using var unauthenticatedRoles = await anonymous
            .CreateClient()
            .GetAsync($"/foundation/companies/{Guid.NewGuid()}/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedMembers.StatusCode);
        Assert.Empty(await unauthenticatedMembers.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedRoles.StatusCode);

        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        // Empresa existente com membership de outro usuário: ator sem membership.
        var nonMemberCompany = await SeedCompanyWithoutActorMembershipAsync(factory);
        using var nonMember = await client.GetAsync(
            $"/foundation/companies/{nonMemberCompany}/members"
        );
        Assert.Equal(HttpStatusCode.Forbidden, nonMember.StatusCode);
        Assert.Equal(
            "{\"code\":\"capability_denied\"}",
            await nonMember.Content.ReadAsStringAsync()
        );

        // Membro com papel, mas sem a capability foundation.roles.assign.
        var scenario = await SeedAuthorizedCompanyAsync(factory, grantRolesAssign: false);
        using var withoutCapability = await client.GetAsync(
            $"/foundation/companies/{scenario.CompanyId}/members"
        );
        using var rolesWithoutCapability = await client.GetAsync(
            $"/foundation/companies/{scenario.CompanyId}/roles"
        );
        Assert.Equal(HttpStatusCode.Forbidden, withoutCapability.StatusCode);
        Assert.Equal(
            "{\"code\":\"capability_denied\"}",
            await withoutCapability.Content.ReadAsStringAsync()
        );
        Assert.Equal(HttpStatusCode.Forbidden, rolesWithoutCapability.StatusCode);

        // Negações geram auditoria Denied dedicada por operação (a tentativa sem
        // membership da seção anterior também audita a listagem de membros).
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var denials = await administrator
            .AuditEvents.Where(audit =>
                audit.Outcome == AuditOutcome.Denied
                && (audit.Action == MembersListAction || audit.Action == RolesListAction))
            .ToListAsync();
        Assert.Equal(3, denials.Count);
        Assert.Equal(2, denials.Count(audit => audit.Action == MembersListAction));
        Assert.Single(denials, audit => audit.Action == RolesListAction);
        Assert.True(
            denials.All(audit =>
                audit.RequestedCompanyId == nonMemberCompany
                || audit.RequestedCompanyId == scenario.CompanyId
            )
        );
    }

    [Fact]
    public async Task Capability_denial_uses_stable_sql_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory, grantRolesAssign: false);

        // Sonda no nível do banco: a negação carrega o SQLSTATE estável do
        // contrato; a mensagem é texto para humanos e não participa da
        // classificação no C#.
        await using var connection = new NpgsqlConnection(
            factory.Fixture.RuntimeConnectionString
        );
        await connection.OpenAsync();
        await using (var applyContext = new NpgsqlCommand(
            "SELECT set_config('app.user_id', $1::text, false), set_config('app.company_id', $2::text, false)",
            connection
        ))
        {
            applyContext.Parameters.AddWithValue(factory.ActorUserId);
            applyContext.Parameters.AddWithValue(scenario.CompanyId);
            await applyContext.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT * FROM foundation.list_company_members_with_roles($1)",
            connection
        );
        command.Parameters.AddWithValue(scenario.CompanyId);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteReaderAsync()
        );
        Assert.Equal(PermissionsSqlStates.CapabilityDenied, exception.SqlState);
        Assert.Equal(
            "member permissions capability is required",
            exception.MessageText
        );
    }

    [Fact]
    public async Task List_returns_roles_with_grants()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var scenario = await SeedAuthorizedCompanyAsync(factory);

        using var response = await factory
            .CreateClient()
            .GetAsync($"/foundation/companies/{scenario.CompanyId}/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        var roles = root.EnumerateArray().ToList();
        Assert.Equal(3, roles.Count);
        Assert.Equal(
            new List<string?> { "Admin", "Viewer", "Zeta" },
            roles.Select(role => role.GetProperty("name").GetString()).ToList()
        );
        Assert.Equal(
            scenario.AdminRoleId,
            roles[0].GetProperty("roleId").GetGuid()
        );
        Assert.Equal(
            new List<string?> { FilesUploadCapability, RolesAssignCapability },
            roles[0]
                .GetProperty("capabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList()
        );
        Assert.Empty(roles[1].GetProperty("capabilities").EnumerateArray());
        Assert.Equal(
            new List<string?> { FilesConfirmCapability },
            roles[2]
                .GetProperty("capabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList()
        );
    }

    private sealed record Scenario(
        Guid CompanyId,
        Guid ActorMembershipId,
        Guid AdminRoleId,
        Guid BrunoMembershipId,
        Guid BrunoUserId);

    private static async Task<Scenario> SeedAuthorizedCompanyAsync(
        FoundationApiFactory factory,
        string companyName = "Company",
        bool grantRolesAssign = true
    )
    {
        var company = await SeedCompanyAsync(factory, companyName);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var actorMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var adminRole = new Role(Guid.NewGuid(), company.Id, "Admin", factory.ActorUserId, Guid.NewGuid());
        var viewerRole = new Role(Guid.NewGuid(), company.Id, "Viewer", factory.ActorUserId, Guid.NewGuid());
        var zetaRole = new Role(Guid.NewGuid(), company.Id, "Zeta", factory.ActorUserId, Guid.NewGuid());
        administrator.AddRange(actorMembership, adminRole, viewerRole, zetaRole);
        administrator.Add(NewAssignment(factory, company.Id, adminRole.Id, actorMembership.Id));

        var bruno = new User(Guid.NewGuid(), "Bruno", "bruno@example.test");
        var carla = new User(Guid.NewGuid(), "Carla", "carla@example.test");
        var brunoMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            bruno.Id,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        var carlaMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            carla.Id,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        administrator.AddRange(bruno, carla, brunoMembership, carlaMembership);
        administrator.Add(NewAssignment(factory, company.Id, viewerRole.Id, brunoMembership.Id));
        await NewCapabilityGrantAsync(adminRole, company, factory, administrator, FilesUploadCapability);
        if (grantRolesAssign)
        {
            await NewCapabilityGrantAsync(adminRole, company, factory, administrator, RolesAssignCapability);
        }
        await NewCapabilityGrantAsync(zetaRole, company, factory, administrator, FilesConfirmCapability);
        await administrator.SaveChangesAsync();
        return new Scenario(
            company.Id,
            actorMembership.Id,
            adminRole.Id,
            brunoMembership.Id,
            bruno.Id);
    }

    private static RoleAssignment NewAssignment(
        FoundationApiFactory factory,
        Guid companyId,
        Guid roleId,
        Guid membershipId
    ) =>
        new(
            Guid.NewGuid(),
            companyId,
            roleId,
            membershipId,
            factory.ActorUserId,
            Guid.NewGuid()
        );

    private static async Task NewCapabilityGrantAsync(
        Role role,
        Company company,
        FoundationApiFactory factory,
        FoundationDbContext administrator,
        string capabilityName
    )
    {
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == capabilityName
        );
        administrator.Add(
            new RoleCapability(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                capability.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                Guid.NewGuid()
            )
        );
    }

    private static async Task<Guid> SeedCompanyWithoutActorMembershipAsync(FoundationApiFactory factory)
    {
        var company = await SeedCompanyAsync(factory, "Non member company");
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var outsider = new User(Guid.NewGuid(), "Davi", "davi@example.test");
        administrator.AddRange(
            outsider,
            new Membership(
                Guid.NewGuid(),
                company.Id,
                outsider.Id,
                factory.ActorUserId,
                Guid.NewGuid()
            )
        );
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<Company> SeedCompanyAsync(FoundationApiFactory factory, string name)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var group = new Group(Guid.NewGuid(), $"Group {name}", factory.ActorUserId, Guid.NewGuid());
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            name,
            factory.ActorUserId,
            Guid.NewGuid()
        );
        administrator.AddRange(group, company);
        await administrator.SaveChangesAsync();
        return company;
    }
}
