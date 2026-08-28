extern alias workerhost;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Microsoft.Extensions.Options;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Platform;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Xunit;
using WorkerProgram = workerhost::Program;
using WorkerHostTypes = workerhost::Vinx.Erp.WorkerHost;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>RED contracts for the worker's trust boundary and durable failure handling.</summary>
public sealed class WorkerHostSecurityContractTests
{
    [Fact]
    public async Task Dead_letter_redaction_preserves_the_operational_type_and_never_fabricates_owner_invitations()
    {
        // Agente: preserva o tipo e o canal, zera o identificador sensível.
        var originalAgentMessageId = Guid.NewGuid();
        var agentEnvelope = new Wolverine.Envelope(
            new Vinx.Erp.Notifications.InboundAgentRequestReceived(originalAgentMessageId, Vinx.Erp.Notifications.AgentChannelKind.WhatsApp));
        WorkerHostTypes.WorkerDeadLetterRedactionInterceptor.RedactEnvelope(agentEnvelope);
        var redactedAgent = Assert.IsType<Vinx.Erp.Notifications.InboundAgentRequestReceived>(agentEnvelope.Message);
        Assert.Equal(Guid.Empty, redactedAgent.InboundMessageId);
        Assert.Equal(Vinx.Erp.Notifications.AgentChannelKind.WhatsApp, redactedAgent.Channel);
        Assert.Empty(agentEnvelope.Headers);
        Assert.DoesNotContain(originalAgentMessageId.ToString("N"), Encoding.UTF8.GetString(agentEnvelope.Data ?? []), StringComparison.Ordinal);

        // Tick de automação: tipo e campos operacionais preservados.
        var tickAt = DateTimeOffset.UtcNow;
        var tickEnvelope = new Wolverine.Envelope(new WorkerHostTypes.DemandasAutomationTick(tickAt, 7));
        WorkerHostTypes.WorkerDeadLetterRedactionInterceptor.RedactEnvelope(tickEnvelope);
        var redactedTick = Assert.IsType<WorkerHostTypes.DemandasAutomationTick>(tickEnvelope.Message);
        Assert.Equal(tickAt, redactedTick.ScheduledAt);
        Assert.Equal(7, redactedTick.Generation);

        // Convite de owner: mantém o tipo com identificador zerado.
        var ownerEnvelope = new Wolverine.Envelope(new OwnerInvitationDeliveryRequested(Guid.NewGuid()));
        WorkerHostTypes.WorkerDeadLetterRedactionInterceptor.RedactEnvelope(ownerEnvelope);
        var redactedOwner = Assert.IsType<OwnerInvitationDeliveryRequested>(ownerEnvelope.Message);
        Assert.Equal(Guid.Empty, redactedOwner.InvitationId);

        // Convite de membro: mantém o tipo com identificador zerado.
        var memberEnvelope = new Wolverine.Envelope(
            new Vinx.Erp.Foundation.Permissions.MemberInvitationDeliveryRequested(Guid.NewGuid()));
        WorkerHostTypes.WorkerDeadLetterRedactionInterceptor.RedactEnvelope(memberEnvelope);
        var redactedMember = Assert.IsType<Vinx.Erp.Foundation.Permissions.MemberInvitationDeliveryRequested>(memberEnvelope.Message);
        Assert.Equal(Guid.Empty, redactedMember.InvitationId);

        // Desconhecido: marcador opaco — nunca um envelope de convite de owner.
        var unknownEnvelope = new Wolverine.Envelope(new UnknownWorkerProbe("conteúdo sensível"));
        var interceptor = new WorkerHostTypes.WorkerDeadLetterRedactionInterceptor();
        WorkerHostTypes.WorkerDeadLetterRedactionInterceptor.RedactEnvelope(unknownEnvelope);
        Assert.IsType<WorkerHostTypes.RedactedUnknownWorkerMessage>(unknownEnvelope.Message);

        var unknownException = await interceptor.BeforeStoreAsync(unknownEnvelope, null, CancellationToken.None);
        var redactionException = Assert.IsType<WorkerHostTypes.WorkerRedactedUnknownDeadLetterException>(unknownException);
        Assert.Equal(WorkerHostTypes.WorkerRedactedUnknownDeadLetterException.UnexpectedExceptionType, redactionException.ExceptionType);
        Assert.DoesNotContain("conteúdo sensível", redactionException.Message, StringComparison.Ordinal);

        // Dono legítimo continua classificado como entrega de owner.
        var ownerException = await interceptor.BeforeStoreAsync(ownerEnvelope, null, CancellationToken.None);
        Assert.IsType<WorkerHostTypes.WorkerOwnerInvitationDeadLetterException>(ownerException);

        // Sem exceção tipada, o fallback do próprio envelope continua na
        // taxonomia de convite de membro, nunca na classificação desconhecida.
        var memberFallbackEnvelope = new Wolverine.Envelope(
            new Vinx.Erp.Foundation.Permissions.MemberInvitationDeliveryRequested(Guid.NewGuid()));
        var memberFallback = await interceptor.BeforeStoreAsync(
            memberFallbackEnvelope,
            null,
            CancellationToken.None);
        var fallbackMemberException = Assert.IsType<WorkerHostTypes.WorkerMemberInvitationDeadLetterException>(memberFallback);
        Assert.Equal(
            WorkerHostTypes.WorkerMemberInvitationDeadLetterException.UnexpectedExceptionType,
            fallbackMemberException.ExceptionType);
        Assert.Equal(
            "Worker member invitation delivery failed unexpectedly.",
            fallbackMemberException.ExceptionMessage);
        var redactedFallbackMember = Assert.IsType<Vinx.Erp.Foundation.Permissions.MemberInvitationDeliveryRequested>(
            memberFallbackEnvelope.Message);
        Assert.Equal(Guid.Empty, redactedFallbackMember.InvitationId);

        // Convite de membro preserva a classificação operacional, mesmo quando
        // a exceção recebida contém detalhes não persistíveis.
        var memberException = await interceptor.BeforeStoreAsync(
            memberEnvelope,
            new WorkerHostTypes.WorkerMemberInvitationDeadLetterException(
                WorkerHostTypes.WorkerMemberInvitationDeadLetterException.RetryExhaustedExceptionType,
                "sensitive retry detail"),
            CancellationToken.None);
        var redactedMemberException = Assert.IsType<WorkerHostTypes.WorkerMemberInvitationDeadLetterException>(memberException);
        Assert.Equal(
            WorkerHostTypes.WorkerMemberInvitationDeadLetterException.RetryExhaustedExceptionType,
            redactedMemberException.ExceptionType);
        Assert.DoesNotContain("sensitive retry detail", redactedMemberException.ExceptionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_host_invalid_malware_scanner_timeout_fails_startup_with_options_validation()
    {
        using var worker = new WorkerHostFactory(
            "Host=127.0.0.1;Port=1;Database=vinx;Username=vinx_worker;Password=test",
            new Dictionary<string, string?>
            {
                ["MalwareScanner:Timeout"] = "00:00:00",
            });

        var failure = Assert.ThrowsAny<Exception>(worker.CreateClient);

        Assert.NotNull(FindException<OptionsValidationException>(failure));
    }

    private sealed record UnknownWorkerProbe(string Payload);

    private static TException? FindException<TException>(Exception failure)
        where TException : Exception
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (current is TException expected)
            {
                return expected;
            }
        }

        return null;
    }

    [Fact]
    public async Task Server_correlation_flows_through_envelope_to_worker_ingress_audit()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        using var response = await api.CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewOrganizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var serverCorrelation = Guid.Parse(response.Headers.GetValues("X-Correlation-Id").Single());

        await using var administrator = await api.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT body FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery WHERE body IS NOT NULL",
            administrator
        );
        var envelopeBody = (byte[]?)await command.ExecuteScalarAsync();
        Assert.NotNull(envelopeBody);
        var envelope = EnvelopeSerializer.Deserialize(envelopeBody);
        Assert.Equal(
            serverCorrelation.ToString("D"),
            envelope.CorrelationId,
            StringComparer.OrdinalIgnoreCase
        );

        var invitationId = await ReadInvitationIdAsync(response);
        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        await WaitForIngressAuditAsync(api.Fixture, invitationId, serverCorrelation);
    }

    [Fact]
    public async Task Worker_role_cannot_read_api_envelopes_or_directly_mutate_foundation()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('vinx_worker', 'vinx_wolverine.wolverine_outgoing_envelopes', 'SELECT'),
                has_table_privilege('vinx_worker', 'foundation.pending_owner_invitations', 'SELECT'),
                has_table_privilege('vinx_worker', 'foundation.pending_owner_invitations', 'UPDATE'),
                has_schema_privilege('vinx_worker', 'vinx_wolverine_queues', 'USAGE'),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound',
                    'DELETE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled',
                    'DELETE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation',
                    'DELETE'
                ),                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation_scheduled',
                    'SELECT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation_scheduled',
                    'INSERT'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation_scheduled',
                    'UPDATE'
                ),
                has_table_privilege(
                    'vinx_worker',
                    'vinx_wolverine_queues.wolverine_queue_demandas_automation_scheduled',
                    'DELETE'
                ),
                has_schema_privilege('vinx_worker', 'vinx_wolverine_queues', 'CREATE'),
                has_database_privilege('vinx_worker', current_database(), 'TEMPORARY'),
                has_database_privilege('vinx_worker', current_database(), 'CREATE')
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        // 23 expressões: telegrafa (4..11) e demandas automation + scheduled
        // (12..19); 20..22 são CREATE schema / TEMPORARY / CREATE database.
        for (var index = 4; index < 20; index++)
        {
            Assert.True(reader.GetBoolean(index), $"worker queue privilege {index} must be granted");
        }

        Assert.False(reader.GetBoolean(20));
        Assert.True(reader.GetBoolean(21));
        Assert.False(reader.GetBoolean(22));
    }

    [Fact]
    public async Task Wolverine_grants_reapply_revokes_cross_schema_and_default_privilege_drift()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();

        await using (
            var contaminate = new NpgsqlCommand(
                """
                GRANT USAGE ON SCHEMA vinx_wolverine_worker TO vinx_runtime;
                GRANT SELECT ON ALL TABLES IN SCHEMA vinx_wolverine_worker TO vinx_runtime;
                ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine_worker
                    GRANT SELECT ON TABLES TO vinx_runtime;
                GRANT USAGE ON SCHEMA vinx_wolverine TO vinx_worker;
                GRANT SELECT ON ALL TABLES IN SCHEMA vinx_wolverine TO vinx_worker;
                ALTER DEFAULT PRIVILEGES FOR ROLE vinx_migrator IN SCHEMA vinx_wolverine
                    GRANT SELECT ON TABLES TO vinx_worker;
                """,
                administrator
            )
        )
        {
            await contaminate.ExecuteNonQueryAsync();
        }

        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var command = new NpgsqlCommand(
            """
            SELECT
                has_schema_privilege('vinx_runtime', 'vinx_wolverine', 'USAGE'),
                has_table_privilege('vinx_runtime', 'vinx_wolverine.wolverine_outgoing_envelopes', 'SELECT'),
                has_schema_privilege('vinx_worker', 'vinx_wolverine_worker', 'USAGE'),
                has_table_privilege('vinx_worker', 'vinx_wolverine_worker.wolverine_incoming_envelopes', 'SELECT'),
                has_schema_privilege('vinx_runtime', 'vinx_wolverine_worker', 'USAGE'),
                has_table_privilege('vinx_runtime', 'vinx_wolverine_worker.wolverine_incoming_envelopes', 'SELECT'),
                has_schema_privilege('vinx_worker', 'vinx_wolverine', 'USAGE'),
                has_table_privilege('vinx_worker', 'vinx_wolverine.wolverine_outgoing_envelopes', 'SELECT'),
                EXISTS (
                    SELECT 1
                    FROM pg_default_acl defaults
                    JOIN pg_roles owner_role ON owner_role.oid = defaults.defaclrole
                    CROSS JOIN LATERAL aclexplode(defaults.defaclacl) privileges
                    JOIN pg_roles grantee ON grantee.oid = privileges.grantee
                    WHERE owner_role.rolname = 'vinx_migrator'
                      AND defaults.defaclnamespace = 'vinx_wolverine_worker'::regnamespace
                      AND grantee.rolname = 'vinx_runtime'
                ),
                EXISTS (
                    SELECT 1
                    FROM pg_default_acl defaults
                    JOIN pg_roles owner_role ON owner_role.oid = defaults.defaclrole
                    CROSS JOIN LATERAL aclexplode(defaults.defaclacl) privileges
                    JOIN pg_roles grantee ON grantee.oid = privileges.grantee
                    WHERE owner_role.rolname = 'vinx_migrator'
                      AND defaults.defaclnamespace = 'vinx_wolverine'::regnamespace
                      AND grantee.rolname = 'vinx_worker'
                )
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("migrator")]
    public async Task Worker_host_rejects_a_non_worker_connection(string role)
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);
        var connectionString =
            role == "runtime" ? fixture.RuntimeConnectionString : fixture.MigratorConnectionString;

        await using var worker = new WorkerHostFactory(connectionString);
        Assert.ThrowsAny<Exception>(() => worker.CreateClient());
    }

    [Fact]
    public async Task Worker_host_registers_redaction_as_a_dead_letter_interceptor()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var worker = new WorkerHostFactory(fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        Assert.Contains(
            worker.Services.GetRequiredService<IEnumerable<IDeadLetterInterceptor>>(),
            interceptor =>
                interceptor.GetType().Name == "WorkerDeadLetterRedactionInterceptor"
        );
    }

    [Fact]
    public async Task Worker_host_startup_fails_closed_without_a_service_role_key()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var worker = new WorkerHostFactory(
            fixture.WorkerConnectionString,
            new Dictionary<string, string?> { ["SUPABASE_AUTH_SERVICE_ROLE_KEY"] = null }
        );
        Assert.ThrowsAny<Exception>(() => worker.CreateClient());
    }

    [Fact]
    public async Task Worker_host_startup_fails_closed_when_redirect_origin_is_not_allowlisted()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var worker = new WorkerHostFactory(
            fixture.WorkerConnectionString,
            new Dictionary<string, string?>
            {
                ["SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS"] = "https://other.example.test",
            }
        );
        Assert.ThrowsAny<Exception>(() => worker.CreateClient());
    }

    [Fact]
    public async Task Worker_host_starts_with_an_allowlisted_redirect_origin()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var worker = new WorkerHostFactory(
            fixture.WorkerConnectionString,
            new Dictionary<string, string?>
            {
                ["SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS"] = "https://app.example.test",
            }
        );
        using var client = worker.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Worker_host_startup_fails_closed_with_a_malformed_allowlist()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);

        await using var worker = new WorkerHostFactory(
            fixture.WorkerConnectionString,
            new Dictionary<string, string?>
            {
                ["SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS"] = "not-an-origin",
            }
        );
        Assert.ThrowsAny<Exception>(() => worker.CreateClient());
    }

    [Fact]
    public async Task Two_worker_hosts_process_duplicate_delivery_with_one_audit()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        using var response = await api.CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewOrganizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitationId = await ReadInvitationIdAsync(response);
        var envelopeId = await ReadQueuedEnvelopeIdAsync(api.Fixture);

        await using var first = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        await using var second = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        await WaitForIngressAuditAsync(api.Fixture, invitationId, expectedCorrelation: null);

        await using var administrator = await api.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE resource_id = @invitation_id AND action = 'owner_invitation.delivery.ingress'",
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Worker_provisions_a_durable_dlq_surface()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        await WolverineStorageDeployment.ApplyAsync(fixture);
        await using var worker = new WorkerHostFactory(fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(jsonb_agg(column_name ORDER BY ordinal_position), '[]'::jsonb)
            FROM information_schema.columns
            WHERE table_schema = 'vinx_wolverine_worker' AND table_name = 'wolverine_dead_letters'
            """,
            administrator
        );
        using var columns = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.Equal(JsonValueKind.Array, columns.RootElement.ValueKind);
        Assert.NotEmpty(columns.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Permanent_worker_failure_is_redacted_into_the_worker_dlq()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        using var response = await api.CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewOrganizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitationId = await ReadInvitationIdAsync(response);
        var envelopeId = await ReadQueuedEnvelopeIdAsync(api.Fixture);

        // Corrupt only the durable receipt after the producer committed. The worker must classify
        // the database invariant failure as permanent and redact the original envelope in its DLQ.
        await using (var administrator = await api.Fixture.OpenAdministratorConnectionAsync())
        await using (
            var corrupt = new NpgsqlCommand(
                """
                WITH replacement_user AS (
                    INSERT INTO foundation.users (id, display_name, email, created_at)
                    VALUES (gen_random_uuid(), 'DLQ test user', 'dlq-' || gen_random_uuid() || '@example.test', now())
                    RETURNING id
                )
                UPDATE foundation.organization_provisioning_receipts
                SET owner_user_id = (SELECT id FROM replacement_user)
                WHERE owner_invitation_id = @invitation_id
                """,
                administrator
            )
        )
        {
            corrupt.Parameters.AddWithValue("invitation_id", invitationId);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        await SetQueuedEnvelopeAttemptsAsync(api.Fixture, envelopeId, attempts: 3);
        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        await WaitForRedactedWorkerDeadLetterAsync(
            api.Fixture,
            invitationId,
            expectedExceptionType: "owner_invitation_delivery_invalid",
            expectedExceptionMessage: "Worker delivery ingress rejected the envelope."
        );
    }

    [Fact]
    public async Task Unexpected_worker_failure_is_redacted_into_the_worker_dlq()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        using var response = await api.CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewOrganizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitationId = await ReadInvitationIdAsync(response);
        var envelopeId = await ReadQueuedEnvelopeIdAsync(api.Fixture);

        await using (var administrator = await api.Fixture.OpenAdministratorConnectionAsync())
        await using (
            var revoke = new NpgsqlCommand(
                "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_worker_owner;",
                administrator
            )
        )
        {
            await revoke.ExecuteNonQueryAsync();
        }

        await SetQueuedEnvelopeAttemptsAsync(api.Fixture, envelopeId, attempts: 3);
        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        await WaitForRedactedWorkerDeadLetterAsync(
            api.Fixture,
            invitationId,
            expectedExceptionType: "owner_invitation_delivery_unexpected",
            expectedExceptionMessage: "Worker delivery ingress failed unexpectedly."
        );
    }

    [Fact]
    public async Task Exhausted_transient_worker_failure_is_redacted_into_the_worker_dlq()
    {
        await using var api = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync(
            aal: "aal2"
        );
        await SeedPlatformOperatorAsync(api);
        using var response = await api.CreateClient()
            .PostAsJsonAsync("/foundation/platform/organizations", NewOrganizationRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invitationId = await ReadInvitationIdAsync(response);
        var envelopeId = await ReadQueuedEnvelopeIdAsync(api.Fixture);
        await ReplaceIngressFunctionWithTransientFailureAsync(api.Fixture);

        await using var worker = new WorkerHostFactory(api.Fixture.WorkerConnectionString);
        using var client = worker.CreateClient();
        await AdvanceScheduledRetryToTerminalAttemptAsync(api.Fixture, envelopeId);
        await WaitForRedactedWorkerDeadLetterAsync(
            api.Fixture,
            invitationId,
            "owner_invitation_delivery_retry_exhausted",
            "Worker delivery ingress exhausted transient retries.",
            TimeSpan.FromSeconds(20)
        );
    }

    private static object NewOrganizationRequest() =>
        new
        {
            requestId = Guid.NewGuid(),
            groupName = $"Worker security group {Guid.NewGuid():N}",
            companyName = $"Worker security company {Guid.NewGuid():N}",
            ownerDisplayName = "Worker security owner",
            ownerEmail = $"worker-security-{Guid.NewGuid():N}@example.test",
        };

    private static async Task<Guid> ReadInvitationIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("ownerInvitationId").GetGuid();
    }

    private static async Task<Guid> ReadQueuedEnvelopeIdAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT body FROM vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery WHERE body IS NOT NULL",
            administrator
        );
        var body = (byte[]?)await command.ExecuteScalarAsync();
        Assert.NotNull(body);
        return EnvelopeSerializer.Deserialize(body).Id;
    }

    private static async Task ReplaceIngressFunctionWithTransientFailureAsync(
        FoundationPostgresFixture fixture
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
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
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AdvanceScheduledRetryToTerminalAttemptAsync(
        FoundationPostgresFixture fixture,
        Guid envelopeId
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!timeout.IsCancellationRequested)
        {
            await using var administrator = await fixture.OpenAdministratorConnectionAsync();
            if (
                await TrySetEnvelopeAttemptsAsync(
                    administrator,
                    "vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery_scheduled",
                    envelopeId,
                    attempts: 3,
                    scheduleImmediately: true,
                    timeout.Token
                )
            )
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }

        Assert.Fail("Worker did not persist a scheduled transient retry.");
    }

    private static async Task SetQueuedEnvelopeAttemptsAsync(
        FoundationPostgresFixture fixture,
        Guid envelopeId,
        int attempts
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        Assert.True(
            await TrySetEnvelopeAttemptsAsync(
                administrator,
                "vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery",
                envelopeId,
                attempts,
                scheduleImmediately: false,
                CancellationToken.None
            )
        );
    }

    private static async Task<bool> TrySetEnvelopeAttemptsAsync(
        NpgsqlConnection administrator,
        string tableName,
        Guid envelopeId,
        int attempts,
        bool scheduleImmediately,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await administrator.BeginTransactionAsync(cancellationToken);
        await using var read = new NpgsqlCommand(
            $"SELECT body FROM {tableName} WHERE id = @envelope_id FOR UPDATE SKIP LOCKED",
            administrator,
            transaction
        );
        read.Parameters.AddWithValue("envelope_id", envelopeId);
        var body = (byte[]?)await read.ExecuteScalarAsync(cancellationToken);
        if (body is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var envelope = EnvelopeSerializer.Deserialize(body);
        envelope.Attempts = attempts;
        var updateStatement = scheduleImmediately
            ? $"UPDATE {tableName} SET body = @body, execution_time = now() WHERE id = @envelope_id"
            : $"UPDATE {tableName} SET body = @body WHERE id = @envelope_id";
        await using var update = new NpgsqlCommand(updateStatement, administrator, transaction);
        update.Parameters.AddWithValue("body", EnvelopeSerializer.Serialize(envelope));
        update.Parameters.AddWithValue("envelope_id", envelopeId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task WaitForIngressAuditAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId,
        Guid? expectedCorrelation
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            await using var administrator = await fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT correlation_id FROM audit.audit_events WHERE resource_id = @invitation_id AND action = 'owner_invitation.delivery.ingress' LIMIT 1",
                administrator
            );
            command.Parameters.AddWithValue("invitation_id", invitationId);
            var value = await command.ExecuteScalarAsync(timeout.Token);
            if (
                value is Guid correlation
                && (expectedCorrelation is null || correlation == expectedCorrelation)
            )
            {
                return;
            }

            await Task.Delay(100, timeout.Token);
        }

        Assert.Fail("Worker ingress audit was not observed with the expected correlation.");
    }

    private static async Task WaitForRedactedWorkerDeadLetterAsync(
        FoundationPostgresFixture fixture,
        Guid originalInvitationId,
        string expectedExceptionType,
        string expectedExceptionMessage,
        TimeSpan? timeoutDuration = null
    )
    {
        using var timeout = new CancellationTokenSource(
            timeoutDuration ?? TimeSpan.FromSeconds(12)
        );
        while (!timeout.IsCancellationRequested)
        {
            await using var administrator = await fixture.OpenAdministratorConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT body, exception_type, exception_message FROM vinx_wolverine_worker.wolverine_dead_letters ORDER BY id DESC LIMIT 1",
                administrator
            );
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            if (await reader.ReadAsync(timeout.Token))
            {
                var body = reader.GetFieldValue<byte[]>(0);
                var envelope = EnvelopeSerializer.Deserialize(body);
                Assert.Equal(expectedExceptionType, reader.GetString(1));
                Assert.Equal(expectedExceptionMessage, reader.GetString(2));
                var bodyText = Encoding.UTF8.GetString(body);
                Assert.DoesNotContain(
                    originalInvitationId.ToString("D"),
                    bodyText,
                    StringComparison.OrdinalIgnoreCase
                );
                Assert.Empty(envelope.Headers);
                Assert.NotNull(envelope.Data);
                var envelopeData = Encoding.UTF8.GetString(envelope.Data!);
                Assert.DoesNotContain(
                    originalInvitationId.ToString("D"),
                    envelopeData,
                    StringComparison.OrdinalIgnoreCase
                );
                Assert.DoesNotContain(
                    "ownerEmail",
                    envelopeData,
                    StringComparison.OrdinalIgnoreCase
                );
                using var payload = JsonDocument.Parse(envelopeData);
                Assert.Equal(Guid.Empty, payload.RootElement.GetProperty("invitationId").GetGuid());

                await reader.CloseAsync();
                await using var legacyAccess = new NpgsqlCommand(
                    "SELECT has_table_privilege('vinx_worker', 'vinx_wolverine.wolverine_dead_letters', 'SELECT')",
                    administrator
                );
                Assert.False((bool)(await legacyAccess.ExecuteScalarAsync())!);
                await AssertTerminalFailureDidNotMutateIngressAsync(fixture, originalInvitationId);
                return;
            }

            await reader.CloseAsync();
            await Task.Delay(100, timeout.Token);
        }

        Assert.Fail("Permanent worker message was not stored in the worker dead-letter table.");
    }

    private static async Task AssertTerminalFailureDidNotMutateIngressAsync(
        FoundationPostgresFixture fixture,
        Guid invitationId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT invitation.status,
                   invitation.provider_pending_at,
                   (
                       SELECT count(*)
                       FROM audit.audit_events
                       WHERE resource_id = invitation.id
                         AND action = 'owner_invitation.delivery.ingress'
                   )
            FROM foundation.pending_owner_invitations AS invitation
            WHERE invitation.id = @invitation_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("invitation_id", invitationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pending_delivery", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0L, reader.GetInt64(2));
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

    private sealed class WorkerHostFactory(
        string? connectionString,
        IReadOnlyDictionary<string, string?>? settings = null
    ) : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("FOUNDATION_WORKER_CONNECTION", connectionString ?? string.Empty);
            builder.UseSetting("SUPABASE_AUTH_ISSUER", "https://project.supabase.test/auth/v1");
            builder.UseSetting("SUPABASE_AUTH_AUDIENCE", "authenticated");
            builder.UseSetting("SUPABASE_AUTH_SERVICE_ROLE_KEY", "test-service-role-key");
            builder.UseSetting("SUPABASE_INVITE_REDIRECT_URL", "https://app.example.test/convite");
            var overrides = settings ?? new Dictionary<string, string?>();
            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }
            builder.ConfigureTestServices(services =>
                services
                    .AddHttpClient(SupabaseAdminInvitationClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new SupabaseInviteHandler())
            );
        }
    }

    private sealed class SupabaseInviteHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
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
