using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class InboundWhatsAppReplyStateFunctionTests
{
    [Fact]
    public async Task Concurrent_workers_have_exactly_one_processing_to_reply_in_flight_winner()
    {
        await using var fixture = await StartFixtureAsync();
        var inboundId = await SeedProcessingInboundAsync(fixture);

        var results = await Task.WhenAll(
            BeginReplyAsync(fixture, inboundId),
            BeginReplyAsync(fixture, inboundId)
        );

        Assert.Equal(1, results.Count(result => result));
        var state = await ReadStateAsync(fixture, inboundId);
        Assert.Equal("reply_in_flight", state.Status);
        Assert.NotNull(state.ReplyStartedAt);
        Assert.Null(state.ReplyMessageId);
        Assert.Null(state.FailureReason);
    }

    [Fact]
    public async Task Reply_in_flight_cannot_be_claimed_and_does_not_expose_target_or_media()
    {
        await using var fixture = await StartFixtureAsync();
        var inboundId = await SeedProcessingInboundAsync(fixture);

        Assert.True(await BeginReplyAsync(fixture, inboundId));

        var claim = await ClaimAsync(fixture, inboundId);

        Assert.Equal("reply_in_flight", claim.Status);
        Assert.Null(claim.WaId);
        Assert.Null(claim.MediaId);
        Assert.Null(claim.Caption);
        Assert.False(claim.Claimed);
    }

    [Fact]
    public async Task Completion_accepts_only_reply_in_flight_and_replays_the_same_wamid_idempotently()
    {
        await using var fixture = await StartFixtureAsync();
        var inboundId = await SeedProcessingInboundAsync(fixture);

        Assert.False(await CompleteAsync(fixture, inboundId, "wamid.reply.before-barrier"));
        Assert.Equal("processing", (await ReadStateAsync(fixture, inboundId)).Status);
        Assert.True(await BeginReplyAsync(fixture, inboundId));

        Assert.True(await CompleteAsync(fixture, inboundId, "wamid.reply.completed"));
        Assert.True(await CompleteAsync(fixture, inboundId, "wamid.reply.completed"));
        Assert.False(await CompleteAsync(fixture, inboundId, "wamid.reply.different"));

        var state = await ReadStateAsync(fixture, inboundId);
        Assert.Equal("completed", state.Status);
        Assert.Equal("wamid.reply.completed", state.ReplyMessageId);
        Assert.Null(state.FailureReason);
    }

    [Theory]
    [InlineData("reply_rejected")]
    [InlineData("reply_ambiguous")]
    public async Task Reply_close_reasons_transition_only_reply_in_flight(string failureReason)
    {
        await using var fixture = await StartFixtureAsync();
        var inboundId = await SeedProcessingInboundAsync(fixture);

        Assert.False(await FailAsync(fixture, inboundId, failureReason));
        Assert.Equal("processing", (await ReadStateAsync(fixture, inboundId)).Status);
        Assert.True(await BeginReplyAsync(fixture, inboundId));

        Assert.True(await FailAsync(fixture, inboundId, failureReason));
        Assert.False(await FailAsync(fixture, inboundId, failureReason));

        var state = await ReadStateAsync(fixture, inboundId);
        Assert.Equal("failed", state.Status);
        Assert.Equal(failureReason, state.FailureReason);
    }

    [Fact]
    public async Task Invalid_transitions_leave_reply_in_flight_unchanged()
    {
        await using var fixture = await StartFixtureAsync();
        var inboundId = await SeedProcessingInboundAsync(fixture);
        Assert.True(await BeginReplyAsync(fixture, inboundId));

        Assert.False(await BeginReplyAsync(fixture, inboundId));
        Assert.False(await FailAsync(fixture, inboundId, "ocr_failed"));

        var state = await ReadStateAsync(fixture, inboundId);
        Assert.Equal("reply_in_flight", state.Status);
        Assert.NotNull(state.ReplyStartedAt);
        Assert.Null(state.ReplyMessageId);
        Assert.Null(state.FailureReason);
    }

    [Fact]
    public async Task Reply_barrier_functions_are_worker_only_and_inbound_rows_have_no_runtime_or_public_dml()
    {
        await using var fixture = await StartFixtureAsync();
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT function_row.proname,
                   pg_get_userbyid(function_row.proowner),
                   function_row.prosecdef,
                   function_row.proconfig,
                   has_function_privilege('vinx_worker', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_runtime', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('public', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                   NOT EXISTS (
                       SELECT 1
                       FROM aclexplode(COALESCE(
                           function_row.proacl,
                           acldefault('f', function_row.proowner))) AS privilege
                       WHERE privilege.privilege_type = 'EXECUTE'
                         AND privilege.grantee NOT IN (
                             function_row.proowner,
                             (SELECT oid FROM pg_roles WHERE rolname = 'vinx_worker')))
            FROM pg_proc AS function_row
            WHERE function_row.oid IN (
                'notifications.begin_inbound_whatsapp_reply(uuid)'::regprocedure,
                'notifications.claim_inbound_whatsapp_message(uuid)'::regprocedure,
                'notifications.complete_inbound_whatsapp_message(uuid,text)'::regprocedure,
                'notifications.fail_inbound_whatsapp_message(uuid,text)'::regprocedure)
            ORDER BY function_row.proname;

            SELECT
                has_table_privilege('vinx_runtime', 'notifications.inbound_whatsapp_messages', 'INSERT, UPDATE, DELETE'),
                has_table_privilege('public', 'notifications.inbound_whatsapp_messages', 'INSERT, UPDATE, DELETE');
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();

        var functionNames = new List<string>();
        while (await reader.ReadAsync())
        {
            functionNames.Add(reader.GetString(0));
            Assert.Equal("vinx_notifications_owner", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal(
                ["search_path=pg_catalog, notifications, pg_temp"],
                reader.GetFieldValue<string[]>(3)
            );
            Assert.True(reader.GetBoolean(4));
            Assert.True(reader.GetBoolean(5));
            Assert.True(reader.GetBoolean(6));
            Assert.True(reader.GetBoolean(7));
            Assert.True(reader.GetBoolean(8));
        }

        Assert.Equal(
            [
                "begin_inbound_whatsapp_reply",
                "claim_inbound_whatsapp_message",
                "complete_inbound_whatsapp_message",
                "fail_inbound_whatsapp_message",
            ],
            functionNames
        );
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    private static async Task<FoundationPostgresFixture> StartFixtureAsync()
    {
        var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task<Guid> SeedProcessingInboundAsync(FoundationPostgresFixture fixture)
    {
        var inboundId = Guid.NewGuid();
        await using (var runtime = new NpgsqlConnection(fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await using var admit = new NpgsqlCommand(
                """
                SELECT notifications.admit_inbound_whatsapp_media(
                    @inbound_id, @phone_number_id, @message_id, @wa_id, 'image', @media_id,
                    NULL, 'unbound', NULL, NULL, NULL)
                """,
                runtime
            );
            admit.Parameters.AddWithValue("inbound_id", inboundId);
            admit.Parameters.AddWithValue("phone_number_id", "111111111111111");
            admit.Parameters.AddWithValue("message_id", $"wamid.reply-state.{inboundId:N}");
            admit.Parameters.AddWithValue("wa_id", "556198132901");
            admit.Parameters.AddWithValue("media_id", $"media-{inboundId:N}");
            Assert.Equal("created", (string?)await admit.ExecuteScalarAsync());
        }

        var claim = await ClaimAsync(fixture, inboundId);
        Assert.Equal("processing", claim.Status);
        Assert.True(claim.Claimed);
        return inboundId;
    }

    private static async Task<bool> BeginReplyAsync(FoundationPostgresFixture fixture, Guid inboundId)
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT notifications.begin_inbound_whatsapp_reply(@inbound_id)",
            worker
        );
        command.Parameters.AddWithValue("inbound_id", inboundId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<InboundClaim> ClaimAsync(
        FoundationPostgresFixture fixture,
        Guid inboundId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, wa_id, media_id, caption, claimed
            FROM notifications.claim_inbound_whatsapp_message(@inbound_id)
            """,
            worker
        );
        command.Parameters.AddWithValue("inbound_id", inboundId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new InboundClaim(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4)
        );
    }

    private static async Task<bool> CompleteAsync(
        FoundationPostgresFixture fixture,
        Guid inboundId,
        string replyMessageId
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT notifications.complete_inbound_whatsapp_message(@inbound_id, @reply_message_id)",
            worker
        );
        command.Parameters.AddWithValue("inbound_id", inboundId);
        command.Parameters.AddWithValue("reply_message_id", replyMessageId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> FailAsync(
        FoundationPostgresFixture fixture,
        Guid inboundId,
        string failureReason
    )
    {
        await using var worker = new NpgsqlConnection(fixture.WorkerConnectionString);
        await worker.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT notifications.fail_inbound_whatsapp_message(@inbound_id, @failure_reason)",
            worker
        );
        command.Parameters.AddWithValue("inbound_id", inboundId);
        command.Parameters.AddWithValue("failure_reason", failureReason);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<InboundState> ReadStateAsync(
        FoundationPostgresFixture fixture,
        Guid inboundId
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, reply_started_at, reply_message_id, failure_reason
            FROM notifications.inbound_whatsapp_messages
            WHERE id = @inbound_id
            """,
            administrator
        );
        command.Parameters.AddWithValue("inbound_id", inboundId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new InboundState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)
        );
    }

    private sealed record InboundClaim(
        string Status,
        string? WaId,
        string? MediaId,
        string? Caption,
        bool Claimed
    );

    private sealed record InboundState(
        string Status,
        DateTimeOffset? ReplyStartedAt,
        string? ReplyMessageId,
        string? FailureReason
    );
}
