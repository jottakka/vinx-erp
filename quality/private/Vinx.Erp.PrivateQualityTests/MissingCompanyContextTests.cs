using Vinx.Erp.PrivateQualityTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.PrivateQualityTests;

/// <summary>
/// Ataques sem contexto: conexões hostis autenticadas como vinx_runtime que não
/// definem nenhum GUC do aplicativo. Falha fechada obrigatória: zero linhas em
/// todas as tabelas de inquilino e escritas rejeitadas pelo WITH CHECK.
/// </summary>
[Collection(PrivatePostgresCollection.Name)]
public sealed class MissingCompanyContextTests
{
    private readonly PrivatePostgresFixture _fixture;

    public MissingCompanyContextTests(PrivatePostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Contexto_ausente_esconde_todos_os_inquilinos_e_rejeita_escrita()
    {
        // Nenhum GUC definido: current_setting(..., true) é nulo e as policies
        // comparam contra nulo — nenhuma linha pode vazar, nenhuma escrita passar.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM foundation.companies"));
        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM foundation.memberships"));
        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM demandas.demands"));
        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM paguai.requests"));
        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM service_desk.atendimento_cards"));

        // Escrita sem contexto precisa violar o WITH CHECK (SQLSTATE 42501),
        // mesmo mirando a própria empresa do atacante.
        var negacao = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            """
            INSERT INTO demandas.demands
                (id, company_id, title, description, responsible_membership_id,
                 requester_user_id, created_by_user_id, updated_by_user_id, request_id)
            VALUES (@id, @companyId, 'Invasao', 'Tentativa de escrita sem contexto',
                    @membershipId, @userId, @userId, @userId, @requestId)
            """,
            new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid(),
                ["companyId"] = _fixture.Seed.AttackerCompanyId,
                ["membershipId"] = _fixture.Seed.AttackerMembershipId,
                ["userId"] = _fixture.Seed.AttackerUserId,
                ["requestId"] = Guid.NewGuid()
            });
        Assert.Equal("42501", negacao.SqlState);
    }

    [Fact]
    public async Task Tabela_de_intencoes_e_inbox_nao_sao_acessiveis_sem_fluxo_do_aplicativo()
    {
        // A tabela de intenções não tem grant direto ao runtime: a leitura só
        // existe pela função SECURITY DEFINER, que exige o ator autenticado.
        var negacaoTabela = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            "SELECT count(*) FROM notifications.notification_intents");
        Assert.Equal("42501", negacaoTabela.SqlState);

        var negacaoInbox = await AbuseAssercoes.EsperarNegacaoAsync(
            _fixture.RuntimeConnectionString,
            "SELECT count(*) FROM notifications.list_inbox_notifications(10, NULL, NULL)");
        Assert.Equal("42501", negacaoInbox.SqlState);
        Assert.Equal("notification actor is required", negacaoInbox.MessageText);
    }
}
