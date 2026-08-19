using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Platform;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class FoundationDbFunctionsExceptionContractTests
{
    [Fact]
    public async Task Transient_ingress_failure_surfaces_the_provider_exception_unwrapped()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        // Sabota a função de ingresso para falhar com 40001 (serialization_failure),
        // o erro que políticas de retry do worker classificam como transitório.
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            CREATE OR REPLACE FUNCTION foundation.accept_owner_invitation_delivery(
                p_invitation_id uuid,
                p_correlation_id uuid)
            RETURNS TABLE (status text, provider_pending_at timestamp with time zone, transitioned boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            BEGIN
                RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'transient worker test failure';
            END;
            $function$;
            """,
            administrator
        ))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using var database = fixture.CreateAdministratorDbContext();
        var ingress = new OwnerInvitationDeliveryIngressService(database);

        var exception = await Assert.ThrowsAnyAsync<NpgsqlException>(() =>
            ingress.AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)
        );

        var postgres = Assert.IsType<PostgresException>(exception);
        Assert.True(postgres.IsTransient);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres.SqlState);
    }
}
