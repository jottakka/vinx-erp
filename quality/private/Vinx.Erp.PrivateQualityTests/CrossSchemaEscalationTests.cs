using Npgsql;
using Vinx.Erp.PrivateQualityTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.PrivateQualityTests;

/// <summary>
/// Ataques de escalada entre schemas: execução de funções SECURITY DEFINER fora
/// do fluxo pretendido, escrita direta nas estruturas do Wolverine pelo papel
/// errado e adulteração da trilha de auditoria append-only.
/// </summary>
[Collection(PrivatePostgresCollection.Name)]
public sealed class CrossSchemaEscalationTests
{
    private readonly PrivatePostgresFixture _fixture;

    public CrossSchemaEscalationTests(PrivatePostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Provisao_de_plataforma_recusa_operador_sem_segundo_fator()
    {
        // Sem nenhum contexto: o ator ausente precisa ser rejeitado de imediato.
        var negacaoSemContexto = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            """
            SELECT * FROM foundation.provision_platform_organization(
                @requestId, 'Grupo Invasor', 'Empresa Invasora', 'Operador Invasor',
                'invasor@example.test', @payloadHash)
            """,
            new Dictionary<string, object>
            {
                ["requestId"] = Guid.NewGuid(),
                ["payloadHash"] = new string('a', 64)
            });
        Assert.Equal("42501", negacaoSemContexto.SqlState);

        // Identidade de operadora forjada com MFA rebaixado para aal1: o portão
        // de garantia aal2 precisa elevar a exceção mesmo para um operador real.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AbuseAssercoes.ForjarContextoAsync(
            connection,
            transaction,
            ("app.user_id", _fixture.Seed.OperatorUserId.ToString()),
            ("app.external_identity_id", _fixture.Seed.OperatorExternalIdentityId.ToString()),
            ("app.mfa_assurance", "aal1"),
            ("app.correlation_id", Guid.NewGuid().ToString()));
        await using var command = new NpgsqlCommand(
            """
            SELECT * FROM foundation.provision_platform_organization(
                @requestId, 'Grupo Invasor', 'Empresa Invasora', 'Operador Invasor',
                'invasor@example.test', @payloadHash)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", Guid.NewGuid());
        command.Parameters.AddWithValue("payloadHash", new string('a', 64));

        var negacaoSemAal2 = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteScalarAsync());
        Assert.Equal("42501", negacaoSemAal2.SqlState);
        Assert.Equal("mfa assurance is required", negacaoSemAal2.MessageText);
    }

    [Fact]
    public async Task Estruturas_do_wolverine_e_trilha_de_auditoria_resistem_aos_papeis_hostis()
    {
        // O runtime não pode fabricar mensagens na durabilidade do worker.
        var negacaoRuntimeNoWorker = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            "INSERT INTO vinx_wolverine_worker.wolverine_dead_letters (id, content) VALUES (@id, @content)",
            new Dictionary<string, object> { ["id"] = Guid.NewGuid(), ["content"] = "{}" });
        Assert.Equal("42501", negacaoRuntimeNoWorker.SqlState);

        // O worker não pode fabricar saídas na durabilidade do ApiHost.
        var negacaoWorkerNoRuntime = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.WorkerConnectionString,
            "INSERT INTO vinx_wolverine.wolverine_outgoing_envelope (id, content) VALUES (@id, @content)",
            new Dictionary<string, object> { ["id"] = Guid.NewGuid(), ["content"] = "{}" });
        Assert.Equal("42501", negacaoWorkerNoRuntime.SqlState);

        // A trilha de auditoria é append-only: UPDATE e DELETE são negados ao runtime.
        var negacaoUpdate = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            "UPDATE audit.audit_events SET action = 'adulterado'");
        Assert.Equal("42501", negacaoUpdate.SqlState);

        var negacaoDelete = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            "DELETE FROM audit.audit_events");
        Assert.Equal("42501", negacaoDelete.SqlState);
    }
}
