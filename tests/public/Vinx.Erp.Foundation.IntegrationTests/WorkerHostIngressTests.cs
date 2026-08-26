extern alias workerhost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Platform;
using Xunit;
using WorkerProgram = workerhost::Program;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class WorkerHostIngressTests
{
    [Fact]
    public async Task Worker_host_sends_the_owner_invitation_once_and_preserves_the_provider_subject()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        var invitation = await ProvisionInvitationAsync(api);

        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var health = worker.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await health.GetAsync("/health")).StatusCode);

        await WaitForPendingAcceptanceAsync(api.Fixture, invitation);
        Assert.Equal(1, await CountIngressAuditsAsync(api.Fixture, invitation));
        Assert.Equal(1, await CountProviderAuditsAsync(api.Fixture, invitation));
        Assert.Equal(1, worker.InviteCalls);
        await AssertProviderBindingAsync(api.Fixture, invitation);

        // The durable message remains replay-safe: provider delivery and audit are one-shot.
        await WaitForPendingAcceptanceAsync(api.Fixture, invitation);
        Assert.Equal(1, await CountIngressAuditsAsync(api.Fixture, invitation));
        Assert.Equal(1, await CountProviderAuditsAsync(api.Fixture, invitation));
        Assert.Equal(1, worker.InviteCalls);
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

    private static async Task WaitForPendingAcceptanceAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string? lastStatus = null;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                await using var administrator = await fixture.OpenAdministratorConnectionAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT status FROM foundation.pending_owner_invitations WHERE id = @invitation_id",
                    administrator
                );
                command.Parameters.AddWithValue("invitation_id", invitationId);
                lastStatus = (string?)await command.ExecuteScalarAsync(timeout.Token);
                if (string.Equals(lastStatus, "pending_acceptance", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }

        Assert.Fail(
            $"Worker did not transition the owner invitation to pending_acceptance; last status was {lastStatus ?? "missing"}."
        );
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

    private static async Task<int> CountProviderAuditsAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE resource_id = @invitation_id AND action = 'owner_invitation.delivery.provider'",
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertProviderBindingAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT provider_issuer, provider_subject FROM foundation.pending_owner_invitations WHERE id = @invitation_id",
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("https://project.supabase.test/auth/v1", reader.GetString(0));
        Assert.Equal(Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190"), reader.GetGuid(1));
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
        private readonly SupabaseInviteHandler _inviteHandler = new();

        public int InviteCalls => _inviteHandler.Calls;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FOUNDATION_WORKER_CONNECTION", connectionString ?? string.Empty);
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.test/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            builder.UseSetting("SUPABASE_AUTH_SERVICE_ROLE_KEY", "test-service-role-key");
            builder.UseSetting("SUPABASE_INVITE_REDIRECT_URL", "https://app.example.test/convite");
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(SupabaseAdminInvitationClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _inviteHandler)
            );
        }
    }

    private sealed class SupabaseInviteHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _calls);
            using var document = JsonDocument.Parse(
                await request.Content!.ReadAsStreamAsync(cancellationToken)
            );
            var email = document.RootElement.GetProperty("email").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new
                    {
                        id = "2a0399f0-a602-4d1f-8f79-22f4bf75f190",
                        aud = "authenticated",
                        email,
                        is_anonymous = false,
                        banned_until = (string?)null,
                        deleted_at = (string?)null,
                    }
                ),
            };
        }
    }
}
