extern alias workerhost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;
using WorkerProgram = workerhost::Program;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class WorkerHostIngressTests
{
    [Fact]
    public async Task Worker_host_consumes_owner_invitation_delivery_and_transitions_once()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        var invitation = await ProvisionInvitationAsync(api);

        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var health = worker.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await health.GetAsync("/health")).StatusCode);

        await WaitForProviderPendingAsync(api.Fixture, invitation);
        Assert.Equal(1, await CountIngressAuditsAsync(api.Fixture, invitation));

        // The durable message remains replay-safe: the state transition and audit are one-shot.
        await WaitForProviderPendingAsync(api.Fixture, invitation);
        Assert.Equal(1, await CountIngressAuditsAsync(api.Fixture, invitation));
    }

    [Fact]
    public async Task Worker_host_fails_closed_when_storage_is_not_provisioned()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();

        await using var worker = new WorkerHostFactory(fixture.WorkerConnectionString);

        Assert.ThrowsAny<Exception>(() => worker.CreateClient());
    }

    private static async Task<Guid> ProvisionInvitationAsync(FoundationApiFactory factory)
    {
        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                "/foundation/platform/organizations",
                new
                {
                    requestId = Guid.NewGuid(),
                    groupName = $"Worker host group {Guid.NewGuid():N}",
                    companyName = $"Worker host company {Guid.NewGuid():N}",
                    ownerDisplayName = "Worker host owner",
                    ownerEmail = $"worker-host-{Guid.NewGuid():N}@example.test",
                }
            );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("ownerInvitationId").GetString()!);
    }

    private static async Task WaitForProviderPendingAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            await using var administrator = await fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status FROM foundation.pending_owner_invitations WHERE id = @invitation_id",
                administrator
            );
            command.Parameters.AddWithValue("invitation_id", invitationId);
            if (
                string.Equals(
                    (string?)await command.ExecuteScalarAsync(timeout.Token),
                    "pending_provider",
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }

        Assert.Fail("Worker did not transition the owner invitation to pending_provider.");
    }

    private static async Task<int> CountIngressAuditsAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE resource_id = @invitation_id AND action = 'owner_invitation.delivery.ingress'",
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task SeedPlatformOperatorAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            SELECT 1, identity_record.id
            FROM foundation.external_identities AS identity_record
            WHERE identity_record.user_id = @user_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("user_id", factory.ActorUserId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class WorkerHostFactory(string? connectionString)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FOUNDATION_WORKER_CONNECTION", connectionString ?? string.Empty);
        }
    }
}
