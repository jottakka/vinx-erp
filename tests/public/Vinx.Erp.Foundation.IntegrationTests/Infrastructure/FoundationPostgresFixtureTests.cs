using System.Net;
using System.Net.Sockets;
using Npgsql;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationPostgresFixtureTests
{
    [Fact]
    public async Task VerifyEndpointAsync_rejects_a_non_postgresql_handshake()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = AcceptSslRequestAndReturnIncompatibleHandshakeAsync(listener);
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Database = "postgres",
            Username = "postgres",
            Password = "fixture-secret-must-not-leak",
            Pooling = false
        };

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FoundationPostgresFixture.VerifyEndpointAsync(
                    settings,
                    expectedEndpoint: null,
                    "endpoint-connect",
                    CancellationToken.None));

            Assert.Contains("phase=endpoint-connect", exception.Message, StringComparison.Ordinal);
            Assert.Contains("host=127.0.0.1", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"port={port}", exception.Message, StringComparison.Ordinal);
            Assert.Contains("NpgsqlException", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(settings.Password, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    [Fact]
    public async Task StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        var administratorSettings = new NpgsqlConnectionStringBuilder(administrator.ConnectionString);
        var migratorSettings = new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString);
        var runtimeSettings = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString);
        Assert.False(administratorSettings.Pooling);
        Assert.False(migratorSettings.Pooling);
        Assert.False(runtimeSettings.Pooling);

        await using var command = new NpgsqlCommand(
            "SELECT current_database(), inet_server_port(), version()", administrator);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0)));
        Assert.Equal(5432, reader.GetInt32(1));
        Assert.StartsWith("PostgreSQL", reader.GetString(2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyEndpointAsync_rejects_postgresql_with_unexpected_database()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        var settings = new NpgsqlConnectionStringBuilder(fixture.AdministratorConnectionString)
        {
            Pooling = false
        };
        var password = settings.Password
            ?? throw new InvalidOperationException("The administrator connection string did not include a password.");
        var unexpectedEndpoint = fixture.Endpoint with
        {
            Database = $"unexpected_{Guid.NewGuid():N}"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundationPostgresFixture.VerifyEndpointAsync(
                settings,
                unexpectedEndpoint,
                "endpoint-connect",
                CancellationToken.None));

        Assert.Contains("phase=endpoint-connect", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyEndpointAsync_rejects_postgresql_with_unexpected_username()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        var settings = new NpgsqlConnectionStringBuilder(fixture.AdministratorConnectionString)
        {
            Pooling = false
        };
        var password = settings.Password
            ?? throw new InvalidOperationException("The administrator connection string did not include a password.");
        var unexpectedEndpoint = fixture.Endpoint with
        {
            Username = $"unexpected_{Guid.NewGuid():N}"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundationPostgresFixture.VerifyEndpointAsync(
                settings,
                unexpectedEndpoint,
                "endpoint-connect",
                CancellationToken.None));

        Assert.Contains("phase=endpoint-connect", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_fixtures_use_distinct_verified_postgresql_endpoints()
    {
        await using var first = new FoundationPostgresFixture();
        await using var second = new FoundationPostgresFixture();

        await Task.WhenAll(first.StartAsync(), second.StartAsync());

        var firstIdentity = await ReadIdentityAsync(first);
        var secondIdentity = await ReadIdentityAsync(second);

        Assert.Equal(first.Endpoint.Database, firstIdentity.Database);
        Assert.Equal(first.Endpoint.Username, firstIdentity.Username);
        Assert.Equal(5432, firstIdentity.Port);
        Assert.StartsWith("PostgreSQL", firstIdentity.Version, StringComparison.Ordinal);
        Assert.Equal(second.Endpoint.Database, secondIdentity.Database);
        Assert.Equal(second.Endpoint.Username, secondIdentity.Username);
        Assert.Equal(5432, secondIdentity.Port);
        Assert.StartsWith("PostgreSQL", secondIdentity.Version, StringComparison.Ordinal);
        Assert.NotEqual(firstIdentity.Database, secondIdentity.Database);
        Assert.NotEqual(first.Endpoint.Port, second.Endpoint.Port);
        var firstSettings = new NpgsqlConnectionStringBuilder(first.AdministratorConnectionString);
        var secondSettings = new NpgsqlConnectionStringBuilder(second.AdministratorConnectionString);
        Assert.False(firstSettings.Pooling);
        Assert.False(secondSettings.Pooling);

        await Task.WhenAll(
            FoundationPostgresFixture.VerifyEndpointAsync(
                firstSettings,
                first.Endpoint,
                "endpoint-connect",
                CancellationToken.None),
            FoundationPostgresFixture.VerifyEndpointAsync(
                secondSettings,
                second.Endpoint,
                "endpoint-connect",
                CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundationPostgresFixture.VerifyEndpointAsync(
                firstSettings,
                second.Endpoint,
                "endpoint-connect",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FoundationPostgresFixture.VerifyEndpointAsync(
                secondSettings,
                first.Endpoint,
                "endpoint-connect",
                CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_grants_public_schema_privileges_only_to_the_migrator()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                role_name,
                has_schema_privilege(role_name, 'public', 'USAGE'),
                has_schema_privilege(role_name, 'public', 'CREATE')
            FROM unnest(ARRAY['vinx_audit_owner', 'vinx_identity_owner', 'vinx_migrator', 'vinx_runtime']) AS role_name
            ORDER BY role_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var privileges = new List<(string Role, bool Usage, bool Create)>();
        while (await reader.ReadAsync())
        {
            privileges.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        Assert.Equal(
        [
            ("vinx_audit_owner", false, false),
            ("vinx_identity_owner", false, false),
            ("vinx_migrator", true, true),
            ("vinx_runtime", false, false)
        ],
        privileges);
    }

    [Fact]
    public async Task ReapplyBootstrapAsync_revokes_preexisting_database_create_from_audit_owner()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var contaminateCommand = new NpgsqlCommand(
            """
            DO $$
            BEGIN
                EXECUTE format(
                    'GRANT CREATE ON DATABASE %I TO vinx_audit_owner',
                    current_database());
            END
            $$;
            """, connection);
        await contaminateCommand.ExecuteNonQueryAsync();
        Assert.True(await AuditOwnerCanCreateAsync(connection));

        await fixture.ReapplyBootstrapAsync();

        Assert.False(await AuditOwnerCanCreateAsync(connection));
    }

    [Fact]
    public async Task StartAsync_creates_the_four_non_superuser_roles_and_migrator_can_set_isolated_owners()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();

        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_roles WHERE rolname = ANY (@roles) ORDER BY rolname", connection);
        command.Parameters.AddWithValue("roles", new[]
        {
            "vinx_audit_owner", "vinx_identity_owner", "vinx_migrator", "vinx_runtime"
        });

        await using var reader = await command.ExecuteReaderAsync();
        var roles = new List<string>();
        while (await reader.ReadAsync()) roles.Add(reader.GetString(0));

        Assert.Equal(["vinx_audit_owner", "vinx_identity_owner", "vinx_migrator", "vinx_runtime"], roles);

        await reader.CloseAsync();
        await using var attributesCommand = new NpgsqlCommand(
            """
            SELECT
                rolname,
                rolcanlogin,
                rolinherit,
                rolbypassrls,
                rolsuper,
                rolcreatedb,
                rolcreaterole
            FROM pg_roles
            WHERE rolname = ANY (@roles)
            ORDER BY rolname
            """, connection);
        attributesCommand.Parameters.AddWithValue("roles", new[]
        {
            "vinx_audit_owner", "vinx_identity_owner", "vinx_migrator", "vinx_runtime"
        });

        await using var attributesReader = await attributesCommand.ExecuteReaderAsync();
        var attributes = new List<(
            string Name,
            bool CanLogin,
            bool Inherit,
            bool BypassRls,
            bool Superuser,
            bool CreateDatabase,
            bool CreateRole)>();
        while (await attributesReader.ReadAsync())
        {
            attributes.Add((
                attributesReader.GetString(0),
                attributesReader.GetBoolean(1),
                attributesReader.GetBoolean(2),
                attributesReader.GetBoolean(3),
                attributesReader.GetBoolean(4),
                attributesReader.GetBoolean(5),
                attributesReader.GetBoolean(6)));
        }

        Assert.Equal(
        [
            ("vinx_audit_owner", false, false, false, false, false, false),
            ("vinx_identity_owner", false, false, false, false, false, false),
            ("vinx_migrator", true, false, false, false, false, false),
            ("vinx_runtime", true, false, false, false, false, false)
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
        Assert.True(await fixture.MigratorCanSetIdentityOwnerAsync());

        await membershipReader.CloseAsync();

        await using var runtimeConnection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtimeConnection.OpenAsync();
        await using var runtimeCommand = new NpgsqlCommand("SELECT current_user", runtimeConnection);
        Assert.Equal("vinx_runtime", await runtimeCommand.ExecuteScalarAsync());
    }

    private static async Task<bool> AuditOwnerCanCreateAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT has_database_privilege('vinx_audit_owner', current_database(), 'CREATE')", connection);
        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The privilege query returned no result."));
    }

    private static async Task<(string Database, string Username, int Port, string Version)> ReadIdentityAsync(
        FoundationPostgresFixture fixture)
    {
        await using var connection = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_database(), current_user, inet_server_port(), version()",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3));
    }

    private static async Task AcceptSslRequestAndReturnIncompatibleHandshakeAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[8];
        await stream.ReadExactlyAsync(request);
        await stream.WriteAsync(new byte[] { (byte)'H' });
        await stream.FlushAsync();
    }
}
