using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class DocumentCoreMigrationDowngradeTests
{
    private const string BeforeDocumentCore = "20260810002901_AddInternalSessionState";

    /// <summary>
    /// O schema legado só conhece o binário is_quarantined: estados liberados
    /// (verified, confirmed) descem como acessíveis e apenas quarentena e falha
    /// de verificação permanecem isolados. O re-up do legado normaliza todo
    /// arquivo acessível para verified — limitação do schema antigo, que não
    /// distinguia confirmed.
    /// </summary>
    [Fact]
    public async Task Downgrade_keeps_released_files_accessible_and_quarantined_files_isolated()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var companyId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var states = new[] { "quarantined", "verified", "confirmed", "verification_failed" };

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            var seed = new NpgsqlCommand(
                """
                INSERT INTO foundation.users (id, display_name, email, created_at, row_version)
                VALUES (@actor, 'Downgrade Ator', @email, now(), 1);
                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@group_id, 'Downgrade', @actor, @actor, @request, now(), now(), 1);
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@company_id, @group_id, 'Downgrade Co', @actor, @actor, @request, now(), now(), 1);
                INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at, row_version)
                VALUES (@membership_id, @company_id, @actor, @actor, @actor, @request, now(), now(), 1);
                """, administrator);
            seed.Parameters.AddWithValue("group_id", Guid.NewGuid());
            seed.Parameters.AddWithValue("company_id", companyId);
            seed.Parameters.AddWithValue("membership_id", membershipId);
            seed.Parameters.AddWithValue("actor", Guid.NewGuid());
            seed.Parameters.AddWithValue("request", Guid.NewGuid());
            seed.Parameters.AddWithValue("email", $"downgrade-{Guid.NewGuid():N}@example.test");
            await seed.ExecuteNonQueryAsync();
        }

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            foreach (var state in states)
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO foundation.files (
                        id, company_id, uploader_membership_id, classification, checksum, provider_key,
                        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at,
                        extension, mime_type, origin, original_file_name, processing_state,
                        retention_until, size_bytes)
                    VALUES (
                        @id, @company_id, @membership_id, 'financial', 'sha256:' || @state,
                        'quarantine/' || replace(@company_id::text, '-', '') || '/' || replace(@id::text, '-', ''),
                        @actor, @actor, @request, now(), now(),
                        '.pdf', 'application/pdf', 'user_upload', 'legacy-downgrade.pdf', @state,
                        now() + interval '30 days', 0)
                    """, administrator);
                command.Parameters.AddWithValue("id", Guid.NewGuid());
                command.Parameters.AddWithValue("company_id", companyId);
                command.Parameters.AddWithValue("membership_id", membershipId);
                command.Parameters.AddWithValue("actor", Guid.NewGuid());
                command.Parameters.AddWithValue("request", Guid.NewGuid());
                command.Parameters.AddWithValue("state", state);
                await command.ExecuteNonQueryAsync();
            }
        }

        await using var migrator = fixture.CreateMigratorDbContext();
        await migrator.Database.MigrateAsync(BeforeDocumentCore);

        // O schema legado não tem processing_state: o checksum carrega o estado
        // original para parear cada linha com o binário is_quarantined.
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "SELECT checksum, is_quarantined FROM foundation.files ORDER BY checksum",
                administrator);
            await using var reader = await command.ExecuteReaderAsync();
            var legacyFlags = new Dictionary<string, bool>();
            while (await reader.ReadAsync())
            {
                legacyFlags[reader.GetString(0)] = reader.GetBoolean(1);
            }

            Assert.Equal(4, legacyFlags.Count);
            Assert.False(legacyFlags["sha256:verified"]);
            Assert.False(legacyFlags["sha256:confirmed"]);
            Assert.True(legacyFlags["sha256:quarantined"]);
            Assert.True(legacyFlags["sha256:verification_failed"]);
        }

        await migrator.Database.MigrateAsync();

        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "SELECT processing_state, count(*) FROM foundation.files GROUP BY processing_state ORDER BY processing_state",
                administrator);
            await using var reader = await command.ExecuteReaderAsync();
            var recovered = new Dictionary<string, long>();
            while (await reader.ReadAsync())
            {
                recovered[reader.GetString(0)] = reader.GetInt64(1);
            }

            // Acessíveis voltam como verified (o legado não distinguia confirmed) e
            // todo isolado volta como quarantined (o legado não distinguia
            // verification_failed) — perda estrutural do schema antigo.
            Assert.Equal(2, recovered["verified"]);
            Assert.Equal(2, recovered["quarantined"]);
            Assert.Equal(0, recovered.GetValueOrDefault("verification_failed"));
            Assert.Equal(4, recovered.Values.Sum());
        }

        // A postura de RLS da tabela volta exatamente como era (FORCE ativo).
        await using (var administrator = await fixture.OpenAdministratorConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT relrowsecurity, relforcerowsecurity
                FROM pg_class
                WHERE oid = 'foundation.files'::regclass
                """, administrator);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
    }
}
