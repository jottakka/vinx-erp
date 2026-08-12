using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipDiscoveryMigrationTests
{
    [Fact]
    public async Task Membership_discovery_returns_only_the_current_actors_active_memberships()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var actorId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();
        var activeCompanyId = Guid.NewGuid();
        var activeMembershipId = Guid.NewGuid();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group_id, 'Grupo', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.users (id, display_name, email, created_at)
            VALUES
                (@actor_id, 'Actor', 'actor-memberships@example.test', clock_timestamp()),
                (@other_actor_id, 'Other actor', 'other-memberships@example.test', clock_timestamp());
            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES
                (@active_company_id, @group_id, 'Ativa', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp()),
                (@other_company_id, @group_id, 'Outra', @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp());
            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, deleted_at)
            VALUES
                (@active_membership_id, @active_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (gen_random_uuid(), @other_company_id, @other_actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), NULL),
                (gen_random_uuid(), @other_company_id, @actor_id, @actor_id, @actor_id, @request_id, clock_timestamp(), clock_timestamp(), clock_timestamp());
            """, administrator))
        {
            seed.Parameters.AddWithValue("group_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("actor_id", actorId);
            seed.Parameters.AddWithValue("other_actor_id", otherActorId);
            seed.Parameters.AddWithValue("request_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("active_company_id", activeCompanyId);
            seed.Parameters.AddWithValue("active_membership_id", activeMembershipId);
            seed.Parameters.AddWithValue("other_company_id", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }

        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var transaction = await runtime.BeginTransactionAsync();
        await using var setActor = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @actor_id, true)", runtime, transaction);
        setActor.Parameters.AddWithValue("actor_id", actorId.ToString());
        await setActor.ExecuteNonQueryAsync();

        await using var discover = new NpgsqlCommand(
            "SELECT membership_id, company_id, company_name FROM foundation.current_actor_memberships()",
            runtime,
            transaction);
        await using var discovered = await discover.ExecuteReaderAsync();

        Assert.True(await discovered.ReadAsync());
        Assert.Equal(activeMembershipId, discovered.GetGuid(0));
        Assert.Equal(activeCompanyId, discovered.GetGuid(1));
        Assert.Equal("Ativa", discovered.GetString(2));
        Assert.False(await discovered.ReadAsync());
    }
}
