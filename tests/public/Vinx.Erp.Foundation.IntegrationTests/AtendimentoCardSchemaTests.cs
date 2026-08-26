using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AtendimentoCardSchemaTests
{
    [Fact]
    public async Task Card_cancellation_constraint_accepts_each_complete_state_and_rejects_every_incomplete_combination()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);
        var cancelledAt = DateTimeOffset.UtcNow;

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        foreach (var accepted in new[]
        {
            new CancellationCardCase("novo", null, null, null, true),
            new CancellationCardCase("em_andamento", null, null, null, true),
            new CancellationCardCase("concluido", null, null, null, true),
            new CancellationCardCase(
                "cancelado",
                cancelledAt,
                seeded.ActorId,
                "Motivo válido.",
                true
            ),
            new CancellationCardCase("backlog", null, null, null, false),
            new CancellationCardCase("cancelado", null, seeded.ActorId, "Motivo válido.", false),
            new CancellationCardCase("cancelado", cancelledAt, null, "Motivo válido.", false),
            new CancellationCardCase("cancelado", cancelledAt, seeded.ActorId, null, false),
            new CancellationCardCase("cancelado", cancelledAt, seeded.ActorId, "   ", false),
        })
        {
            var insert = InsertCardAsync(administrator, seeded, accepted);
            if (accepted.Accepted)
            {
                await insert;
                continue;
            }

            var exception = await Assert.ThrowsAsync<PostgresException>(() => insert);
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        foreach (var column in new[] { "novo", "em_andamento", "concluido" })
        {
            foreach (var incomplete in new[]
            {
                new CancellationCardCase(column, cancelledAt, null, null, false),
                new CancellationCardCase(column, null, seeded.ActorId, null, false),
                new CancellationCardCase(column, null, null, "Não permitido.", false),
            })
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                    InsertCardAsync(administrator, seeded, incomplete)
                );
                Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            }
        }
    }

    [Fact]
    public async Task History_constraints_accept_cancelled_from_and_to_values_and_reject_unknown_values()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        var columns = new[] { "novo", "em_andamento", "concluido", "cancelado" };
        foreach (var fromColumn in columns)
        {
            foreach (var toColumn in columns)
            {
                await InsertHistoryAsync(administrator, seeded, fromColumn, toColumn);
            }
        }

        foreach (var invalid in new[]
        {
            (FromColumn: "desconhecido", ToColumn: "novo"),
            (FromColumn: "novo", ToColumn: "desconhecido"),
        })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertHistoryAsync(administrator, seeded, invalid.FromColumn, invalid.ToColumn)
            );
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM service_desk.atendimento_card_history WHERE card_id = @card_id",
            administrator
        );
        count.Parameters.AddWithValue("card_id", seeded.CardAId);
        Assert.Equal(16L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Runtime_rls_reads_only_cards_for_the_transaction_company_and_never_without_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
            );
            var visible = await runtime
                .Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM service_desk.atendimento_cards ORDER BY id"
                )
                .ToListAsync();

            Assert.Equal([seeded.CardAId], visible);
            Assert.DoesNotContain(seeded.CardBId, visible);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime
                .Database.SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM service_desk.atendimento_cards ORDER BY id"
                )
                .ToListAsync();

            Assert.Empty(withoutContext);
        }
    }

    [Fact]
    public async Task Runtime_rls_reads_participants_only_for_the_transaction_company_and_never_without_context()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
            );
            var visible = await runtime.Database.SqlQuery<Guid>(
                $"SELECT membership_id AS \"Value\" FROM service_desk.atendimento_card_participants ORDER BY membership_id"
            ).ToListAsync();

            Assert.Equal([seeded.MembershipAId], visible);
            Assert.DoesNotContain(seeded.MembershipBId, visible);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime.Database.SqlQuery<Guid>(
                $"SELECT membership_id AS \"Value\" FROM service_desk.atendimento_card_participants ORDER BY membership_id"
            ).ToListAsync();

            Assert.Empty(withoutContext);
        }
    }

    [Fact]
    public async Task Runtime_has_no_delete_grant_for_atendimento_tables()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
        );

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM service_desk.atendimento_cards WHERE id = {seeded.CardAId}"
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Runtime_has_no_update_or_delete_grant_for_atendimento_participants()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
        );

        var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM service_desk.atendimento_card_participants WHERE card_id = {seeded.CardAId}"
            )
        );

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Runtime_can_insert_same_company_participants_but_rls_rejects_cross_company_rows()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var runtime = fixture.CreateRuntimeDbContext();
        await using var transaction = await runtime.Database.BeginTransactionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true)"
        );
        await runtime.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id) VALUES ({seeded.CompanyAId}, {seeded.CardAId}, {seeded.AdditionalMembershipAId})"
        );

        var crossCompany = await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id) VALUES ({seeded.CompanyBId}, {seeded.CardBId}, {seeded.MembershipBId})"
            )
        );
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossCompany.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Participant_composite_foreign_keys_are_exact_restrict_constraints_and_reject_cross_company_pairs_for_an_administrator()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        var foreignCard = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteParticipantInsertAsync(
                administrator,
                seeded.CompanyAId,
                seeded.CardBId,
                seeded.MembershipAId
            )
        );
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignCard.SqlState);

        var foreignMembership = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteParticipantInsertAsync(
                administrator,
                seeded.CompanyAId,
                seeded.CardAId,
                seeded.MembershipBId
            )
        );
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignMembership.SqlState);

        await using (var deleteCard = new NpgsqlCommand(
            "DELETE FROM service_desk.atendimento_cards WHERE id = @card_id",
            administrator
        ))
        {
            deleteCard.Parameters.AddWithValue("card_id", seeded.CardAId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                deleteCard.ExecuteNonQueryAsync()
            );
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }

        await using (var deleteMembership = new NpgsqlCommand(
            "DELETE FROM foundation.memberships WHERE id = @membership_id",
            administrator
        ))
        {
            deleteMembership.Parameters.AddWithValue("membership_id", seeded.MembershipAId);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                deleteMembership.ExecuteNonQueryAsync()
            );
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }

        await using var catalog = new NpgsqlCommand(
            """
            SELECT
                constraint_row.conname,
                source_schema.nspname,
                source_table.relname,
                array_agg(source_column.attname ORDER BY source_key.ordinality),
                target_schema.nspname,
                target_table.relname,
                array_agg(target_column.attname ORDER BY source_key.ordinality),
                constraint_row.confdeltype::text
            FROM pg_constraint AS constraint_row
            INNER JOIN pg_class AS source_table ON source_table.oid = constraint_row.conrelid
            INNER JOIN pg_namespace AS source_schema ON source_schema.oid = source_table.relnamespace
            INNER JOIN pg_class AS target_table ON target_table.oid = constraint_row.confrelid
            INNER JOIN pg_namespace AS target_schema ON target_schema.oid = target_table.relnamespace
            INNER JOIN unnest(constraint_row.conkey) WITH ORDINALITY AS source_key(attnum, ordinality)
                ON TRUE
            INNER JOIN pg_attribute AS source_column
                ON source_column.attrelid = source_table.oid
               AND source_column.attnum = source_key.attnum
            INNER JOIN unnest(constraint_row.confkey) WITH ORDINALITY AS target_key(attnum, ordinality)
                ON target_key.ordinality = source_key.ordinality
            INNER JOIN pg_attribute AS target_column
                ON target_column.attrelid = target_table.oid
               AND target_column.attnum = target_key.attnum
            WHERE constraint_row.conrelid = 'service_desk.atendimento_card_participants'::regclass
              AND constraint_row.contype = 'f'
            GROUP BY
                constraint_row.conname,
                source_schema.nspname,
                source_table.relname,
                target_schema.nspname,
                target_table.relname,
                constraint_row.confdeltype
            ORDER BY constraint_row.conname
            """,
            administrator
        );
        await using var reader = await catalog.ExecuteReaderAsync();
        var foreignKeys = new List<ParticipantForeignKey>();
        while (await reader.ReadAsync())
        {
            foreignKeys.Add(
                new ParticipantForeignKey(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<string[]>(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetFieldValue<string[]>(6),
                    reader.GetString(7)
                )
            );
        }

        Assert.Collection(
            foreignKeys,
            key => AssertParticipantForeignKey(
                key,
                "fk_atendimento_card_participants_atendimento_cards_company_id_",
                ["company_id", "card_id"],
                "service_desk",
                "atendimento_cards",
                ["company_id", "id"]
            ),
            key => AssertParticipantForeignKey(
                key,
                "fk_atendimento_card_participants_memberships_company_id_member",
                ["company_id", "membership_id"],
                "foundation",
                "memberships",
                ["company_id", "id"]
            )
        );
    }

    [Fact]
    public async Task Assignment_options_function_is_security_definer_scoped_and_does_not_grant_runtime_user_reads()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var seeded = await SeedCardsAsync(fixture);

        await using (var runtime = fixture.CreateRuntimeDbContext())
        await using (var transaction = await runtime.Database.BeginTransactionAsync())
        {
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.company_id', {seeded.CompanyAId.ToString()}, true); SELECT set_config('app.user_id', {seeded.ActorId.ToString()}, true);"
            );
            var options = await runtime.Database.SqlQuery<AssignmentOption>(
                $"SELECT membership_id, display_name FROM foundation.current_company_membership_options()"
            ).ToListAsync();

            Assert.Equal(
                [
                    new AssignmentOption(seeded.MembershipAId, "Atendimento actor"),
                    new AssignmentOption(
                        seeded.AdditionalMembershipAId,
                        "Participante adicional"
                    ),
                ],
                options
            );
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var directRead = await Assert.ThrowsAsync<PostgresException>(async () =>
                await runtime.Database.SqlQuery<string>(
                    $"SELECT display_name AS \"Value\" FROM foundation.users"
                ).ToListAsync()
            );
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, directRead.SqlState);
        }

        await using (var runtime = fixture.CreateRuntimeDbContext())
        {
            var withoutContext = await runtime.Database.SqlQuery<AssignmentOption>(
                $"SELECT membership_id, display_name FROM foundation.current_company_membership_options()"
            ).ToListAsync();
            Assert.Empty(withoutContext);
        }

        await using var administrator = await fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT p.prosecdef, array_to_string(p.proconfig, '|'), r.rolname, has_function_privilege('vinx_runtime', p.oid, 'EXECUTE'), NOT has_function_privilege('vinx_migrator', p.oid, 'EXECUTE'), NOT EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) AS acl WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'), (SELECT c.relforcerowsecurity FROM pg_class c WHERE c.oid = 'service_desk.atendimento_card_participants'::regclass), (SELECT count(*) FROM pg_constraint c WHERE c.conrelid = 'service_desk.atendimento_card_participants'::regclass AND c.contype = 'f' AND c.confdeltype = 'r') FROM pg_proc p INNER JOIN pg_namespace n ON n.oid = p.pronamespace INNER JOIN pg_roles r ON r.oid = p.proowner WHERE n.nspname = 'foundation' AND p.proname = 'current_company_membership_options';",
            administrator
        );
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("search_path=foundation, pg_catalog", reader.GetString(1));
        Assert.Equal("vinx_authorization_owner", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.Equal(2L, reader.GetInt64(7));
    }

    private static async Task<SeededCards> SeedCardsAsync(FoundationPostgresFixture fixture)
    {
        await using var administrator = fixture.CreateAdministratorDbContext();
        var actorId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Grupo Atendimento", actorId, requestId);
        var companyA = new Company(Guid.NewGuid(), group.Id, "Empresa A", actorId, requestId);
        var companyB = new Company(Guid.NewGuid(), group.Id, "Empresa B", actorId, requestId);
        var actor = new User(actorId, "Atendimento actor", "atendimento-actor@example.test");
        var membershipA = new Membership(
            Guid.NewGuid(),
            companyA.Id,
            actorId,
            actorId,
            Guid.NewGuid()
        );
        var membershipB = new Membership(
            Guid.NewGuid(),
            companyB.Id,
            actorId,
            actorId,
            Guid.NewGuid()
        );
        var additionalUser = new User(
            Guid.NewGuid(),
            "Participante adicional",
            "additional-participant@example.test"
        );
        var additionalMembershipA = new Membership(
            Guid.NewGuid(),
            companyA.Id,
            additionalUser.Id,
            actorId,
            Guid.NewGuid()
        );
        administrator.AddRange(
            actor,
            group,
            companyA,
            companyB,
            membershipA,
            membershipB,
            additionalUser,
            additionalMembershipA
        );
        await administrator.SaveChangesAsync();

        var cardAId = Guid.NewGuid();
        var cardBId = Guid.NewGuid();
        await administrator.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO service_desk.atendimento_cards
                (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
            VALUES
                ({cardAId}, {companyA.Id}, 'Cartão A', 'Empresa A.', 'novo', {actorId}, {actorId}, {Guid.NewGuid()}),
                ({cardBId}, {companyB.Id}, 'Cartão B', 'Empresa B.', 'novo', {actorId}, {actorId}, {Guid.NewGuid()});
            INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id)
            VALUES
                ({companyA.Id}, {cardAId}, {membershipA.Id}),
                ({companyB.Id}, {cardBId}, {membershipB.Id});
            """
        );
        return new SeededCards(
            companyA.Id,
            companyB.Id,
            cardAId,
            cardBId,
            actorId,
            membershipA.Id,
            membershipB.Id,
            additionalMembershipA.Id
        );
    }

    private static async Task InsertCardAsync(
        NpgsqlConnection administrator,
        SeededCards seeded,
        CancellationCardCase card
    )
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_cards
                (id, company_id, title, description, column_name, cancelled_at, cancelled_by_user_id, cancellation_reason, created_by_user_id, updated_by_user_id, request_id)
            VALUES
                (@id, @company_id, 'Matriz de cancelamento', 'Validação de banco.', @column_name, @cancelled_at, @cancelled_by_user_id, @cancellation_reason, @actor_id, @actor_id, @request_id)
            """,
            administrator
        );
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("company_id", seeded.CompanyAId);
        insert.Parameters.AddWithValue("column_name", card.Column);
        insert.Parameters.AddWithValue("cancelled_at", (object?)card.CancelledAt ?? DBNull.Value);
        insert.Parameters.AddWithValue(
            "cancelled_by_user_id",
            (object?)card.CancelledByUserId ?? DBNull.Value
        );
        insert.Parameters.AddWithValue("cancellation_reason", (object?)card.Reason ?? DBNull.Value);
        insert.Parameters.AddWithValue("actor_id", seeded.ActorId);
        insert.Parameters.AddWithValue("request_id", Guid.NewGuid());
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task InsertHistoryAsync(
        NpgsqlConnection administrator,
        SeededCards seeded,
        string fromColumn,
        string toColumn
    )
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_card_history
                (id, company_id, card_id, from_column, to_column, actor_user_id, request_id)
            VALUES
                (@id, @company_id, @card_id, @from_column, @to_column, @actor_id, @request_id)
            """,
            administrator
        );
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("company_id", seeded.CompanyAId);
        insert.Parameters.AddWithValue("card_id", seeded.CardAId);
        insert.Parameters.AddWithValue("from_column", fromColumn);
        insert.Parameters.AddWithValue("to_column", toColumn);
        insert.Parameters.AddWithValue("actor_id", seeded.ActorId);
        insert.Parameters.AddWithValue("request_id", Guid.NewGuid());
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteParticipantInsertAsync(
        NpgsqlConnection administrator,
        Guid companyId,
        Guid cardId,
        Guid membershipId
    )
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO service_desk.atendimento_card_participants (company_id, card_id, membership_id)
            VALUES (@company_id, @card_id, @membership_id)
            """,
            administrator
        );
        insert.Parameters.AddWithValue("company_id", companyId);
        insert.Parameters.AddWithValue("card_id", cardId);
        insert.Parameters.AddWithValue("membership_id", membershipId);
        await insert.ExecuteNonQueryAsync();
    }

    private static void AssertParticipantForeignKey(
        ParticipantForeignKey key,
        string expectedName,
        IReadOnlyList<string> expectedSourceColumns,
        string expectedTargetSchema,
        string expectedTargetTable,
        IReadOnlyList<string> expectedTargetColumns
    )
    {
        Assert.Equal(expectedName, key.Name);
        Assert.Equal("service_desk", key.SourceSchema);
        Assert.Equal("atendimento_card_participants", key.SourceTable);
        Assert.Equal(expectedSourceColumns, key.SourceColumns);
        Assert.Equal(expectedTargetSchema, key.TargetSchema);
        Assert.Equal(expectedTargetTable, key.TargetTable);
        Assert.Equal(expectedTargetColumns, key.TargetColumns);
        Assert.Equal("r", key.DeleteAction);
    }

    private sealed record AssignmentOption(Guid MembershipId, string DisplayName);

    private sealed record CancellationCardCase(
        string Column,
        DateTimeOffset? CancelledAt,
        Guid? CancelledByUserId,
        string? Reason,
        bool Accepted
    );

    private sealed record ParticipantForeignKey(
        string Name,
        string SourceSchema,
        string SourceTable,
        IReadOnlyList<string> SourceColumns,
        string TargetSchema,
        string TargetTable,
        IReadOnlyList<string> TargetColumns,
        string DeleteAction
    );

    private sealed record SeededCards(
        Guid CompanyAId,
        Guid CompanyBId,
        Guid CardAId,
        Guid CardBId,
        Guid ActorId,
        Guid MembershipAId,
        Guid MembershipBId,
        Guid AdditionalMembershipAId
    );
}
