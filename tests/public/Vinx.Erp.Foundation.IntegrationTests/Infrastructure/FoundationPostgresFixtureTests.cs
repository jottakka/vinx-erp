using Npgsql;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationPostgresFixtureTests
{
    [Fact]
    public async Task StartAsync_creates_the_three_non_superuser_roles_and_migrator_can_set_audit_owner()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_roles WHERE rolname = ANY (@roles) ORDER BY rolname", connection);
        command.Parameters.AddWithValue("roles", new[]
        {
            "vinx_audit_owner", "vinx_migrator", "vinx_runtime"
        });

        await using var reader = await command.ExecuteReaderAsync();
        var roles = new List<string>();
        while (await reader.ReadAsync()) roles.Add(reader.GetString(0));

        Assert.Equal(["vinx_audit_owner", "vinx_migrator", "vinx_runtime"], roles);

        await reader.CloseAsync();
        await using var attributesCommand = new NpgsqlCommand(
            """
            SELECT rolname, rolcanlogin, rolinherit, rolbypassrls
            FROM pg_roles
            WHERE rolname = ANY (@roles)
            ORDER BY rolname
            """, connection);
        attributesCommand.Parameters.AddWithValue("roles", new[]
        {
            "vinx_audit_owner", "vinx_migrator", "vinx_runtime"
        });

        await using var attributesReader = await attributesCommand.ExecuteReaderAsync();
        var attributes = new List<(string Name, bool CanLogin, bool Inherit, bool BypassRls)>();
        while (await attributesReader.ReadAsync())
        {
            attributes.Add((
                attributesReader.GetString(0),
                attributesReader.GetBoolean(1),
                attributesReader.GetBoolean(2),
                attributesReader.GetBoolean(3)));
        }

        Assert.Equal(
        [
            ("vinx_audit_owner", false, false, false),
            ("vinx_migrator", true, false, false),
            ("vinx_runtime", true, false, false)
        ],
        attributes);

        await attributesReader.CloseAsync();
        await using var grantsCommand = new NpgsqlCommand(
            """
            SELECT
                has_database_privilege('vinx_migrator', current_database(), 'CONNECT'),
                has_database_privilege('vinx_migrator', current_database(), 'CREATE'),
                has_database_privilege('vinx_runtime', current_database(), 'CONNECT'),
                has_database_privilege('vinx_runtime', current_database(), 'CREATE')
            """, connection);
        await using var grantsReader = await grantsCommand.ExecuteReaderAsync();
        Assert.True(await grantsReader.ReadAsync());
        Assert.True(grantsReader.GetBoolean(0));
        Assert.True(grantsReader.GetBoolean(1));
        Assert.True(grantsReader.GetBoolean(2));
        Assert.False(grantsReader.GetBoolean(3));

        await grantsReader.CloseAsync();
        await using var membershipCommand = new NpgsqlCommand(
            """
            SELECT membership.inherit_option, membership.set_option
            FROM pg_auth_members AS membership
            INNER JOIN pg_roles AS role ON role.oid = membership.roleid
            INNER JOIN pg_roles AS member ON member.oid = membership.member
            WHERE role.rolname = 'vinx_audit_owner' AND member.rolname = 'vinx_migrator'
            """, connection);
        await using var membershipReader = await membershipCommand.ExecuteReaderAsync();
        Assert.True(await membershipReader.ReadAsync());
        Assert.False(membershipReader.GetBoolean(0));
        Assert.True(membershipReader.GetBoolean(1));

        Assert.True(await fixture.MigratorCanSetAuditOwnerAsync());

        await membershipReader.CloseAsync();
        await using var runtimeConnection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtimeConnection.OpenAsync();
        await using var runtimeCommand = new NpgsqlCommand("SELECT current_user", runtimeConnection);
        Assert.Equal("vinx_runtime", await runtimeCommand.ExecuteScalarAsync());
    }
}
