using Vinx.Erp.PrivateQualityTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.PrivateQualityTests;

/// <summary>
/// Ataques de contexto forjado: um papel de banco conectado (vinx_runtime) tenta
/// se passar por inquilino legítimo apenas escrevendo GUCs do aplicativo. As
/// policies de RLS precisam falhar fechado quando o conjunto autorizado não foi
/// resolvido pelo servidor; qualquer linha da empresa vítima visível é vazamento.
/// </summary>
[Collection(PrivatePostgresCollection.Name)]
public sealed class ForgedCompanyContextTests
{
    private readonly PrivatePostgresFixture _fixture;

    public ForgedCompanyContextTests(PrivatePostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Contexto_de_empresa_forjado_nao_expoe_membros_demandas_nem_cartoes_de_outra_empresa()
    {
        // app.company_id da vítima com app.authorized_company_ids ausente: o GUC
        // sozinho não pode autorizar a leitura entre empresas.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AbuseAssercoes.ForjarContextoAsync(
            connection,
            transaction,
            ("app.company_id", _fixture.Seed.VictimCompanyId.ToString()));

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
                connection, transaction, "SELECT count(*) FROM service_desk.atendimento_cards"));
    }

    [Fact]
    public async Task Contexto_de_empresa_forjado_nao_expoe_pedidos_paguai_de_outra_empresa()
    {
        // paguai.requests isola por empresa E requerente: a empresa forjada não
        // basta se o requerente não coincide com app.user_id.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AbuseAssercoes.ForjarContextoAsync(
            connection,
            transaction,
            ("app.company_id", _fixture.Seed.VictimCompanyId.ToString()));

        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM paguai.requests"));
    }

    [Fact]
    public async Task Conjunto_autorizado_forjado_nao_expoe_empresas_nem_membros_de_outra_empresa()
    {
        // Espelha ApplyAuthorizedReadAsync (empresa limpa + lista autorizada), mas
        // com a lista forjada: a leitura ampliada precisa continuar fechada para
        // um papel que não passou pela resolução de memberships do servidor.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AbuseAssercoes.ForjarContextoAsync(
            connection,
            transaction,
            ("app.company_id", string.Empty),
            ("app.authorized_company_ids", _fixture.Seed.VictimCompanyId.ToString()));

        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM foundation.companies"));
        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection, transaction, "SELECT count(*) FROM foundation.memberships"));
    }

    [Fact]
    public async Task Inbox_de_outra_empresa_nao_e_lida_com_usuario_forjado()
    {
        // A caixa de entrada só é acessível pela função SECURITY DEFINER; forjar o
        // usuário e a empresa da vítima não pode trazer as intenções dela.
        await using var connection = await _fixture.OpenConnectionAsync(_fixture.RuntimeConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AbuseAssercoes.ForjarContextoAsync(
            connection,
            transaction,
            ("app.user_id", _fixture.Seed.VictimUserId.ToString()),
            ("app.company_id", _fixture.Seed.VictimCompanyId.ToString()));

        Assert.Equal(
            0L,
            await AbuseAssercoes.ContarLinhasAsync(
                connection,
                transaction,
                "SELECT count(*) FROM notifications.list_inbox_notifications(10, NULL, NULL)"));
    }
}
