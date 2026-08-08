using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditTransactionTests
{
    [Fact]
    public async Task Failed_success_audit_rolls_back_the_role_assignment_and_returns_a_stable_audit_error()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await RoleAssignmentServiceIntegrationTests.SeedScenarioAsync(factory);
        await RevokeAuditAppendAsync(factory);

        var result = await RoleAssignmentServiceIntegrationTests.InvokeAssignmentAsync(
            factory,
            scenario.CompanyId,
            scenario.AssignableRoleId,
            scenario.TargetMembershipId,
            Guid.NewGuid());

        Assert.Equal("audit_unavailable", RoleAssignmentServiceIntegrationTests.ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(administrator.RoleAssignments.Where(assignment =>
            assignment.RoleId == scenario.AssignableRoleId && assignment.MembershipId == scenario.TargetMembershipId));
    }

    [Fact]
    public async Task Failed_denial_audit_returns_a_stable_audit_error_without_exposing_database_details()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        await RevokeAuditAppendAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/foundation/capabilities");
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Contains("audit_unavailable", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgres", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }
}
