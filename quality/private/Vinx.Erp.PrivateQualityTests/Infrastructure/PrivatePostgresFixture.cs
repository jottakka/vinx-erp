using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.PrivateQualityTests.Infrastructure;

/// <summary>
/// Fixture única do suite privado: um Postgres 17 real, migrado pelo
/// FoundationDbContext (papel vinx_migrator) e preparado com os mesmos papéis
/// e grants do harness público. Nenhum superusuário é usado nos ataques;
/// as conexões hostis são vinx_runtime e vinx_worker.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PrivatePostgresCollection : ICollectionFixture<PrivatePostgresFixture>
{
    public const string Name = "private-quality-postgres";
}

public sealed class PrivatePostgresFixture : IAsyncLifetime
{
    private const string BootstrapRolesResourceName =
        "Vinx.Erp.PrivateQualityTests.Infrastructure.bootstrap-foundation-roles.sql";
    private const string WolverineGrantsResourceName =
        "Vinx.Erp.PrivateQualityTests.Infrastructure.grant-wolverine-runtime.sql";
    private const string AdministratorUsername = "postgres";

    private readonly PostgreSqlContainer _container;
    private string? _administratorConnectionString;
    private string? _migratorConnectionString;
    private string? _runtimeConnectionString;
    private string? _workerConnectionString;

    public PrivatePostgresFixture()
    {
        var database = $"vinx_private_{Guid.NewGuid():N}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _container = new PostgreSqlBuilder("postgres:17.9-alpine")
            .WithDatabase(database)
            .WithUsername(AdministratorUsername)
            .WithPassword(password)
            .Build();
    }

    public string AdministratorConnectionString => _administratorConnectionString
        ?? throw new InvalidOperationException("A fixture não foi iniciada.");

    public string MigratorConnectionString => _migratorConnectionString
        ?? throw new InvalidOperationException("A fixture não foi iniciada.");

    public string RuntimeConnectionString => _runtimeConnectionString
        ?? throw new InvalidOperationException("A fixture não foi iniciada.");

    public string WorkerConnectionString => _workerConnectionString
        ?? throw new InvalidOperationException("A fixture não foi iniciada.");

    /// <summary>Identificadores do cenário semeado: empresa A (do ator) e empresa B (vítima).</summary>
    public PrivateSeedData Seed { get; private set; } = null!;

    public async Task<NpgsqlConnection> OpenConnectionAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var containerSettings = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        _administratorConnectionString = containerSettings.ConnectionString;

        await using (var administrator = await OpenConnectionAsync(AdministratorConnectionString))
        {
            await ExecuteResourceAsync(administrator, BootstrapRolesResourceName);
        }

        var migratorPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var runtimePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var workerPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using (var administrator = await OpenConnectionAsync(AdministratorConnectionString))
        {
            await SetRolePasswordAsync(administrator, "vinx_migrator", migratorPassword);
            await SetRolePasswordAsync(administrator, "vinx_runtime", runtimePassword);
            await SetRolePasswordAsync(administrator, "vinx_worker", workerPassword);
        }

        _migratorConnectionString = BuildConnectionString("vinx_migrator", migratorPassword);
        _runtimeConnectionString = BuildConnectionString("vinx_runtime", runtimePassword);
        _workerConnectionString = BuildConnectionString("vinx_worker", workerPassword);

        await MigrateAsync();
        await DeployWolverineSkeletonAsync();
        Seed = await SeedScenarioAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Migração executada pelo mesmo caminho do harness público: EF sobre o papel migrador.</summary>
    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(MigratorConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new FoundationDbContext(options);
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Esqueleto estrutural mínimo dos schemas do Wolverine (durabilidade do ApiHost
    /// em vinx_wolverine, do WorkerHost em vinx_wolverine_worker e filas duráveis em
    /// vinx_wolverine_queues), reproduzindo o estado implantado pelo `db-apply` dos
    /// hosts para exercitar a fronteira de grants do grant-wolverine-runtime.sql sem
    /// depender do deploy completo do Wolverine.
    /// </summary>
    private async Task DeployWolverineSkeletonAsync()
    {
        const string skeleton = """
            CREATE SCHEMA vinx_wolverine;
            CREATE SCHEMA vinx_wolverine_worker;
            CREATE SCHEMA vinx_wolverine_queues;

            CREATE TABLE vinx_wolverine.wolverine_incoming_envelope (id uuid PRIMARY KEY, content jsonb);
            CREATE TABLE vinx_wolverine.wolverine_outgoing_envelope (id uuid PRIMARY KEY, content jsonb);
            CREATE TABLE vinx_wolverine.wolverine_dead_letters (id uuid PRIMARY KEY, content jsonb);

            CREATE TABLE vinx_wolverine_worker.wolverine_incoming_envelope (id uuid PRIMARY KEY, content jsonb);
            CREATE TABLE vinx_wolverine_worker.wolverine_outgoing_envelope (id uuid PRIMARY KEY, content jsonb);
            CREATE TABLE vinx_wolverine_worker.wolverine_dead_letters (id uuid PRIMARY KEY, content jsonb);

            CREATE TABLE vinx_wolverine_queues.wolverine_queue_demandas_automation (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_demandas_automation_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_member_invitation_delivery (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_member_invitation_delivery_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_notification_delivery (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_notification_delivery_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_owner_invitation_delivery_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_paguai_events (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_paguai_events_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_telegram_inbound (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_telegram_inbound_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_whatsapp_inbound (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_documents (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_documents_scheduled (id uuid PRIMARY KEY, body jsonb);
            CREATE TABLE vinx_wolverine_queues.wolverine_queue_whatsapp_inbound_scheduled (id uuid PRIMARY KEY, body jsonb);
            """;
        await using (var migrator = await OpenConnectionAsync(MigratorConnectionString))
        {
            await using var skeletonCommand = new NpgsqlCommand(skeleton, migrator);
            await skeletonCommand.ExecuteNonQueryAsync();
            await ExecuteResourceAsync(migrator, WolverineGrantsResourceName);
        }
    }

    /// <summary>
    /// Cenário vítima semeado como administrador (postgres): empresas A e B do mesmo
    /// grupo, usuários e memberships, demandas/cartões/pedidos/intenções da empresa B
    /// e um operador de plataforma. Os ataques tentam alcançar exatamente estas linhas.
    /// </summary>
    private async Task<PrivateSeedData> SeedScenarioAsync()
    {
        var seed = PrivateSeedData.Create();
        var sql = """
            INSERT INTO foundation.users (id, display_name, email, created_at) VALUES
                (@userA, 'Ana Atacante', 'ana-atacante@example.test', clock_timestamp()),
                (@userB, 'Bruno Vitima', 'bruno-vitima@example.test', clock_timestamp()),
                (@operatorUser, 'Operadora de Plataforma', 'operadora@example.test', clock_timestamp());

            INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
            VALUES (@group, 'Grupo Semente', @operatorUser, @operatorUser, @request, clock_timestamp(), clock_timestamp());

            INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES
                (@companyA, @group, 'Empresa Alfa', @operatorUser, @operatorUser, @request, clock_timestamp(), clock_timestamp()),
                (@companyB, @group, 'Empresa Beta', @operatorUser, @operatorUser, @request, clock_timestamp(), clock_timestamp());

            INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at) VALUES
                (@membershipA, @companyA, @userA, @operatorUser, @operatorUser, @request, clock_timestamp(), clock_timestamp()),
                (@membershipB, @companyB, @userB, @operatorUser, @operatorUser, @request, clock_timestamp(), clock_timestamp());

            INSERT INTO foundation.external_identities (id, user_id, issuer, subject, created_at)
            VALUES (@operatorIdentity, @operatorUser, 'test', 'operadora-semente', clock_timestamp());

            INSERT INTO foundation.platform_operators (singleton, external_identity_id)
            VALUES (1, @operatorIdentity);

            INSERT INTO demandas.demand_routines (id, company_id, name, created_by_user_id, updated_by_user_id, request_id)
            VALUES (@routineB, @companyB, 'Rotina Beta', @userB, @userB, @request);

            INSERT INTO demandas.demands (id, company_id, title, description, responsible_membership_id, requester_user_id, created_by_user_id, updated_by_user_id, request_id)
            VALUES (@demandB, @companyB, 'Demanda confidencial', 'Descricao confidencial da empresa Beta', @membershipB, @userB, @userB, @userB, @request);

            INSERT INTO service_desk.atendimento_cards (id, company_id, title, description, column_name, created_by_user_id, updated_by_user_id, request_id)
            VALUES (@cardB, @companyB, 'Cartao confidencial', 'Descricao confidencial do cartao', 'novo', @userB, @userB, @request);

            INSERT INTO paguai.requests (id, company_id, requester_user_id, request_id, title, description)
            VALUES (@paguaiB, @companyB, @userB, @request, 'Pedido confidencial', 'Descricao confidencial do pedido');

            INSERT INTO notifications.notification_intents (id, company_id, recipient_user_id, event_key, channel, template_key, status, provider_attempt_id, provider_resolved_at, correlation_id, created_by_user_id)
            VALUES (@intentB, @companyB, @userB, 'evento-confidencial-beta', 'in_app', 'modelo-inbox', 'delivered', gen_random_uuid(), clock_timestamp(), @request, @userB);
            """;
        await using var administrator = await OpenConnectionAsync(AdministratorConnectionString);
        await using var command = new NpgsqlCommand(sql, administrator);
        command.Parameters.AddWithValue("userA", seed.AttackerUserId);
        command.Parameters.AddWithValue("userB", seed.VictimUserId);
        command.Parameters.AddWithValue("operatorUser", seed.OperatorUserId);
        command.Parameters.AddWithValue("operatorIdentity", seed.OperatorExternalIdentityId);
        command.Parameters.AddWithValue("group", seed.GroupId);
        command.Parameters.AddWithValue("request", seed.RequestId);
        command.Parameters.AddWithValue("companyA", seed.AttackerCompanyId);
        command.Parameters.AddWithValue("companyB", seed.VictimCompanyId);
        command.Parameters.AddWithValue("membershipA", seed.AttackerMembershipId);
        command.Parameters.AddWithValue("membershipB", seed.VictimMembershipId);
        command.Parameters.AddWithValue("routineB", seed.VictimRoutineId);
        command.Parameters.AddWithValue("demandB", seed.VictimDemandId);
        command.Parameters.AddWithValue("cardB", seed.VictimCardId);
        command.Parameters.AddWithValue("paguaiB", seed.VictimPaguaiRequestId);
        command.Parameters.AddWithValue("intentB", seed.VictimInboxIntentId);
        await command.ExecuteNonQueryAsync();
        return seed;
    }

    private static async Task ExecuteResourceAsync(NpgsqlConnection connection, string resourceName)
    {
        var assembly = typeof(PrivatePostgresFixture).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"O recurso {resourceName} não foi embutido.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();
        await using var command = new NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetRolePasswordAsync(NpgsqlConnection connection, string roleName, string password)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var passwordCommand = new NpgsqlCommand(
            "SELECT set_config('vinx.bootstrap.password', @password, true);", connection, transaction);
        passwordCommand.Parameters.AddWithValue("password", password);
        await passwordCommand.ExecuteNonQueryAsync();

        await using var alterRoleCommand = new NpgsqlCommand(
            $"""
            DO $$
            BEGIN
                EXECUTE format('ALTER ROLE {roleName} PASSWORD %L', current_setting('vinx.bootstrap.password'));
            END
            $$;
            """, connection, transaction);
        await alterRoleCommand.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private string BuildConnectionString(string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdministratorConnectionString)
        {
            Username = username,
            Password = password,
            Pooling = false
        };
        return builder.ConnectionString;
    }
}

/// <summary>Linhas sementes do cenário de abuso (empresa A = atacante, empresa B = vítima).</summary>
public sealed record PrivateSeedData(
    Guid GroupId,
    Guid RequestId,
    Guid AttackerCompanyId,
    Guid AttackerUserId,
    Guid AttackerMembershipId,
    Guid VictimCompanyId,
    Guid VictimUserId,
    Guid VictimMembershipId,
    Guid VictimRoutineId,
    Guid VictimDemandId,
    Guid VictimCardId,
    Guid VictimPaguaiRequestId,
    Guid VictimInboxIntentId,
    Guid OperatorUserId,
    Guid OperatorExternalIdentityId)
{
    public static PrivateSeedData Create() => new(
        GroupId: Guid.NewGuid(),
        RequestId: Guid.NewGuid(),
        AttackerCompanyId: Guid.NewGuid(),
        AttackerUserId: Guid.NewGuid(),
        AttackerMembershipId: Guid.NewGuid(),
        VictimCompanyId: Guid.NewGuid(),
        VictimUserId: Guid.NewGuid(),
        VictimMembershipId: Guid.NewGuid(),
        VictimRoutineId: Guid.NewGuid(),
        VictimDemandId: Guid.NewGuid(),
        VictimCardId: Guid.NewGuid(),
        VictimPaguaiRequestId: Guid.NewGuid(),
        VictimInboxIntentId: Guid.NewGuid(),
        OperatorUserId: Guid.NewGuid(),
        OperatorExternalIdentityId: Guid.NewGuid());
}
