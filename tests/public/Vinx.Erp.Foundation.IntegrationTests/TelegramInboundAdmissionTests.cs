using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// BDD da fronteira SQL de admissão Telegram: a resolução é fechada, a primeira
/// evidência é imutável e apenas funções SECURITY DEFINER são expostas ao runtime.
/// </summary>
public sealed class TelegramInboundAdmissionTests
{
    private const string BeforeInboundTelegramTextAdmission =
        "20260826121000_AddTelegramOutboundTarget";
    private const long MaximumPrivateChatId = 4_503_599_627_370_495;

    [Fact]
    public async Task Validated_private_chats_resolve_and_admit_only_their_own_tenant_identity()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var first = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810001",
            telegramContactValidated: true
        );
        var second = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810002",
            telegramContactValidated: true
        );

        var firstResolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 810001);
        var secondResolution = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 810002);

        AssertAdmitted(firstResolution, first.RecipientUserId, first.CompanyId);
        AssertAdmitted(secondResolution, second.RecipientUserId, second.CompanyId);
        Assert.NotEqual(firstResolution.MembershipId, secondResolution.MembershipId);
        Assert.NotEqual(firstResolution.CompanyId, secondResolution.CompanyId);

        Assert.Equal(
            "created",
            await AdmitAsync(
                factory,
                AdmissionRequest.Admitted(810001, 1, 1001, "first tenant", firstResolution)
            )
        );
        Assert.Equal(
            "created",
            await AdmitAsync(
                factory,
                AdmissionRequest.Admitted(810002, 2, 1002, "second tenant", secondResolution)
            )
        );

        var rows = await TelegramNotificationSeed.ReadInboundMessagesAsync(factory);
        Assert.Equal(2, rows.Count);
        var firstRow = rows.Single(row => row.ChatId == 810001);
        var secondRow = rows.Single(row => row.ChatId == 810002);
        Assert.Equal(first.CompanyId, firstRow.ResolvedCompanyId);
        Assert.Equal(first.RecipientUserId, firstRow.ResolvedUserId);
        Assert.Equal(firstResolution.MembershipId, firstRow.ResolvedMembershipId);
        Assert.Equal(second.CompanyId, secondRow.ResolvedCompanyId);
        Assert.Equal(second.RecipientUserId, secondRow.ResolvedUserId);
        Assert.Equal(secondResolution.MembershipId, secondRow.ResolvedMembershipId);
        Assert.All(rows, row =>
        {
            Assert.Equal("pending", row.Status);
            Assert.Equal("admitted", row.ResolutionState);
            Assert.Null(row.CompletedAt);
        });
    }

    [Fact]
    public async Task Resolver_executes_every_closed_branch_without_exposing_tenant_ids()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "820001",
            telegramContactValidated: false
        );
        var revokedMembership = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "820002",
            telegramContactValidated: true
        );
        var deletedCompany = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "820003",
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "820004",
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "820004",
            telegramContactValidated: true
        );
        await TelegramNotificationSeed.RevokeMembershipAsync(
            factory,
            revokedMembership.CompanyId,
            revokedMembership.RecipientUserId
        );
        await DeleteCompanyAsync(factory, deletedCompany.CompanyId);

        AssertClosed("unbound", await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 820000));
        AssertClosed("unverified", await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 820001));
        AssertClosed("inactive", await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 820002));
        AssertClosed("inactive", await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 820003));
        AssertClosed("ambiguous", await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 820004));
    }

    [Fact]
    public async Task Admission_rejects_invalid_ids_text_limits_and_resolution_tuples()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var valid = AdmissionRequest.Unbound(830001, 1, 2001, "valid text");
        var invalid = new[]
        {
            valid with { InboundId = null },
            valid with { UpdateId = null },
            valid with { UpdateId = 0 },
            valid with { UpdateId = -1 },
            valid with { ChatId = null },
            valid with { ChatId = 0 },
            valid with { ChatId = -1 },
            valid with { ChatId = MaximumPrivateChatId + 1 },
            valid with { MessageId = null },
            valid with { MessageId = 0 },
            valid with { MessageId = -1 },
            valid with { TextBody = "" },
            valid with { TextBody = " \t\r\n" },
            valid with { ResolutionState = "admitted" },
            valid with { UserId = Guid.NewGuid() },
        };

        foreach (var request in invalid)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => AdmitAsync(factory, request));
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
        }

        foreach (var chatId in new long?[] { null, 0, -1, MaximumPrivateChatId + 1 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ResolveStateAsync(factory, chatId)
            );
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
        }

        var longest = AdmissionRequest.Unbound(830002, 2, 2002, new string('a', 4096));
        Assert.Equal("created", await AdmitAsync(factory, longest));
        var tooLong = longest with
        {
            InboundId = Guid.NewGuid(),
            ChatId = 830003,
            MessageId = 2003,
            TextBody = longest.TextBody + "b",
        };
        var overLimit = await Assert.ThrowsAsync<PostgresException>(() => AdmitAsync(factory, tooLong));
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, overLimit.SqlState);

        var rows = await TelegramNotificationSeed.ReadInboundMessagesAsync(factory);
        var row = Assert.Single(rows);
        Assert.Equal(4096, row.TextBody.Length);
        Assert.Equal("unbound", row.ResolutionState);
    }

    [Fact]
    public async Task Exact_replay_and_conflict_preserve_the_first_closed_evidence_after_later_validation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var first = AdmissionRequest.Unbound(840001, 9, 3001, "first closed evidence");
        Assert.Equal("created", await AdmitAsync(factory, first));

        var laterContact = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "840001",
            telegramContactValidated: true
        );
        var admitted = await TelegramNotificationSeed.ResolveInboundActorAsync(factory, 840001);
        AssertAdmitted(admitted, laterContact.RecipientUserId, laterContact.CompanyId);

        var replay = first with
        {
            InboundId = Guid.NewGuid(),
            ResolutionState = admitted.ResolutionState,
            UserId = admitted.UserId,
            CompanyId = admitted.CompanyId,
            MembershipId = admitted.MembershipId,
        };
        Assert.Equal("replay", await AdmitAsync(factory, replay));
        Assert.Equal(
            "conflict",
            await AdmitAsync(
                factory,
                replay with { InboundId = Guid.NewGuid(), UpdateId = 10, TextBody = "changed" }
            )
        );

        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(first.InboundId!.Value, row.Id);
        Assert.Equal(9, row.UpdateId);
        Assert.Equal("first closed evidence", row.TextBody);
        Assert.Equal("unbound", row.ResolutionState);
        Assert.Null(row.ResolvedUserId);
        Assert.Null(row.ResolvedCompanyId);
        Assert.Null(row.ResolvedMembershipId);
        Assert.Equal("pending", row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task Rolled_back_admission_leaves_no_evidence_and_allows_a_new_created_result()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var request = AdmissionRequest.Unbound(850001, 11, 4001, "rolled back");

        await using (var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString))
        {
            await runtime.OpenAsync();
            await using var transaction = await runtime.BeginTransactionAsync();
            Assert.Equal("created", await AdmitAsync(runtime, transaction, request));
            await transaction.RollbackAsync();
        }

        Assert.Empty(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("created", await AdmitAsync(factory, request with { InboundId = Guid.NewGuid() }));
        Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
    }

    [Fact]
    public async Task Concurrent_duplicate_admission_returns_created_and_replay_without_a_unique_violation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var template = AdmissionRequest.Unbound(860001, 12, 5001, "concurrent");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(AdmitConcurrentlyAsync));

        Assert.Contains("created", results);
        Assert.Contains("replay", results);
        Assert.DoesNotContain("conflict", results);
        var row = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal(template.ChatId, row.ChatId);
        Assert.Equal(template.MessageId, row.MessageId);
        Assert.Equal("unbound", row.ResolutionState);

        async Task<string> AdmitConcurrentlyAsync(int attempt)
        {
            await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
            await runtime.OpenAsync();
            if (Interlocked.Increment(ref ready) == 2)
            {
                release.TrySetResult(true);
            }

            await release.Task;
            return await AdmitAsync(runtime, null, template with { InboundId = Guid.NewGuid() });
        }
    }

    [Fact]
    public async Task Migration_up_and_empty_down_create_then_remove_only_the_inbound_admission_contract()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await using var migrator = fixture.CreateMigratorDbContext();

        await migrator.Database.MigrateAsync(BeforeInboundTelegramTextAdmission);
        await AssertAdmissionContractExistsAsync(fixture, expected: false);

        await migrator.Database.MigrateAsync();
        await AssertAdmissionContractExistsAsync(fixture, expected: true);

        await migrator.Database.MigrateAsync(BeforeInboundTelegramTextAdmission);
        await AssertAdmissionContractExistsAsync(fixture, expected: false);
    }

    [Fact]
    public async Task Migration_down_refuses_to_discard_durable_inbound_evidence()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var request = AdmissionRequest.Unbound(870001, 13, 6001, "downgrade guard");
        Assert.Equal("created", await AdmitAsync(fixture.RuntimeConnectionString, request));

        await using var migrator = fixture.CreateMigratorDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.Database.MigrateAsync(BeforeInboundTelegramTextAdmission)
        );
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await AssertAdmissionContractExistsAsync(fixture, expected: true);
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        Assert.Equal(
            1L,
            (long)(await ScalarAsync(
                administrator,
                "SELECT count(*) FROM notifications.inbound_telegram_messages"
            ))!
        );
    }

    [Fact]
    public async Task Runtime_has_only_the_two_secure_definer_functions_and_no_direct_inbound_table_access()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var grants = new NpgsqlCommand(
            """
            SELECT
                pg_get_userbyid(table_row.relowner),
                table_row.relrowsecurity,
                table_row.relforcerowsecurity,
                has_table_privilege('vinx_runtime', table_row.oid, 'SELECT'),
                has_table_privilege('vinx_runtime', table_row.oid, 'INSERT'),
                has_table_privilege('vinx_runtime', table_row.oid, 'UPDATE'),
                has_table_privilege('vinx_runtime', table_row.oid, 'DELETE'),
                has_function_privilege(
                    'vinx_runtime',
                    'notifications.resolve_inbound_telegram_actor(bigint)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_runtime',
                    'notifications.admit_inbound_telegram_text(uuid,bigint,bigint,bigint,text,text,uuid,uuid,uuid)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_worker',
                    'notifications.resolve_inbound_telegram_actor(bigint)',
                    'EXECUTE'),
                has_function_privilege(
                    'vinx_worker',
                    'notifications.admit_inbound_telegram_text(uuid,bigint,bigint,bigint,text,text,uuid,uuid,uuid)',
                    'EXECUTE'),
                to_regprocedure('notifications.complete_inbound_telegram_text(uuid)') IS NULL
            FROM pg_class AS table_row
            WHERE table_row.oid = 'notifications.inbound_telegram_messages'::regclass
            """,
            administrator
        );
        await using (var reader = await grants.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("vinx_notifications_owner", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            for (var index = 3; index <= 6; index++)
            {
                Assert.False(reader.GetBoolean(index));
            }

            Assert.True(reader.GetBoolean(7));
            Assert.True(reader.GetBoolean(8));
            Assert.False(reader.GetBoolean(9));
            Assert.False(reader.GetBoolean(10));
            Assert.True(reader.GetBoolean(11));
            Assert.False(await reader.ReadAsync());
        }

        await AssertFunctionContractAsync(
            administrator,
            "notifications.resolve_inbound_telegram_actor(bigint)",
            "vinx_runtime"
        );
        await AssertFunctionContractAsync(
            administrator,
            "notifications.admit_inbound_telegram_text(uuid,bigint,bigint,bigint,text,text,uuid,uuid,uuid)",
            "vinx_runtime"
        );

        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync(runtime, "SELECT count(*) FROM notifications.inbound_telegram_messages")
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private static void AssertAdmitted(
        TelegramInboundActorResolution resolution,
        Guid expectedUserId,
        Guid expectedCompanyId
    )
    {
        Assert.Equal("admitted", resolution.ResolutionState);
        Assert.Equal(expectedUserId, resolution.UserId);
        Assert.Equal(expectedCompanyId, resolution.CompanyId);
        Assert.NotNull(resolution.MembershipId);
    }

    private static void AssertClosed(string expectedState, TelegramInboundActorResolution resolution)
    {
        Assert.Equal(expectedState, resolution.ResolutionState);
        Assert.Null(resolution.UserId);
        Assert.Null(resolution.CompanyId);
        Assert.Null(resolution.MembershipId);
    }

    private static async Task DeleteCompanyAsync(FoundationApiFactory factory, Guid companyId)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE foundation.companies SET deleted_at = clock_timestamp() WHERE id = @company_id",
            administrator
        );
        command.Parameters.AddWithValue("company_id", companyId);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<string> AdmitAsync(FoundationApiFactory factory, AdmissionRequest request) =>
        AdmitAsync(factory.Fixture.RuntimeConnectionString, request);

    private static async Task<string> AdmitAsync(string runtimeConnectionString, AdmissionRequest request)
    {
        await using var runtime = new NpgsqlConnection(runtimeConnectionString);
        await runtime.OpenAsync();
        return await AdmitAsync(runtime, null, request);
    }

    private static async Task<string> ResolveStateAsync(
        FoundationApiFactory factory,
        long? chatId
    )
    {
        await using var runtime = new NpgsqlConnection(factory.Fixture.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT resolution_state FROM notifications.resolve_inbound_telegram_actor(@chat_id)",
            runtime
        );
        AddParameter(command, "chat_id", NpgsqlDbType.Bigint, chatId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> AdmitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AdmissionRequest request
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT notifications.admit_inbound_telegram_text(
                @inbound_id, @update_id, @chat_id, @message_id, @text_body,
                @resolution_state, @user_id, @company_id, @membership_id)
            """,
            connection,
            transaction
        );
        AddParameter(command, "inbound_id", NpgsqlDbType.Uuid, request.InboundId);
        AddParameter(command, "update_id", NpgsqlDbType.Bigint, request.UpdateId);
        AddParameter(command, "chat_id", NpgsqlDbType.Bigint, request.ChatId);
        AddParameter(command, "message_id", NpgsqlDbType.Bigint, request.MessageId);
        AddParameter(command, "text_body", NpgsqlDbType.Text, request.TextBody);
        AddParameter(command, "resolution_state", NpgsqlDbType.Text, request.ResolutionState);
        AddParameter(command, "user_id", NpgsqlDbType.Uuid, request.UserId);
        AddParameter(command, "company_id", NpgsqlDbType.Uuid, request.CompanyId);
        AddParameter(command, "membership_id", NpgsqlDbType.Uuid, request.MembershipId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value
    ) => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private static async Task AssertAdmissionContractExistsAsync(
        FoundationPostgresFixture fixture,
        bool expected
    )
    {
        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                to_regclass('notifications.inbound_telegram_messages') IS NOT NULL,
                to_regprocedure('notifications.resolve_inbound_telegram_actor(bigint)') IS NOT NULL,
                to_regprocedure(
                    'notifications.admit_inbound_telegram_text(uuid,bigint,bigint,bigint,text,text,uuid,uuid,uuid)') IS NOT NULL
            """,
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected, reader.GetBoolean(0));
        Assert.Equal(expected, reader.GetBoolean(1));
        Assert.Equal(expected, reader.GetBoolean(2));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertFunctionContractAsync(
        NpgsqlConnection connection,
        string signature,
        params string[] executors
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_userbyid(function_row.proowner),
                   function_row.prosecdef,
                   function_row.proconfig,
                   NOT EXISTS (
                       SELECT 1 FROM unnest(@executors::text[]) AS executor(role_name)
                       WHERE NOT has_function_privilege(executor.role_name, function_row.oid, 'EXECUTE')),
                   NOT has_function_privilege('public', function_row.oid, 'EXECUTE'),
                   NOT has_function_privilege('vinx_migrator', function_row.oid, 'EXECUTE'),
                   NOT EXISTS (
                       SELECT 1
                       FROM aclexplode(COALESCE(
                           function_row.proacl,
                           acldefault('f', function_row.proowner))) AS privilege
                       WHERE privilege.privilege_type = 'EXECUTE'
                         AND privilege.grantee <> function_row.proowner
                         AND privilege.grantee <> ALL (
                             SELECT oid FROM pg_roles WHERE rolname = ANY(@executors)))
            FROM pg_proc AS function_row
            WHERE function_row.oid = to_regprocedure(@signature)
            """,
            connection
        );
        command.Parameters.AddWithValue("executors", executors);
        command.Parameters.AddWithValue("signature", signature);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("vinx_notifications_owner", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(
            ["search_path=pg_catalog, notifications, pg_temp"],
            reader.GetFieldValue<string[]>(2)
        );
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        string sql
    )
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed record AdmissionRequest(
        Guid? InboundId,
        long? UpdateId,
        long? ChatId,
        long? MessageId,
        string? TextBody,
        string? ResolutionState,
        Guid? UserId,
        Guid? CompanyId,
        Guid? MembershipId
    )
    {
        public static AdmissionRequest Unbound(
            long chatId,
            long updateId,
            long messageId,
            string textBody
        ) => new(
            Guid.NewGuid(),
            updateId,
            chatId,
            messageId,
            textBody,
            "unbound",
            null,
            null,
            null
        );

        public static AdmissionRequest Admitted(
            long chatId,
            long updateId,
            long messageId,
            string textBody,
            TelegramInboundActorResolution resolution
        ) => new(
            Guid.NewGuid(),
            updateId,
            chatId,
            messageId,
            textBody,
            resolution.ResolutionState,
            resolution.UserId,
            resolution.CompanyId,
            resolution.MembershipId
        );
    }
}
