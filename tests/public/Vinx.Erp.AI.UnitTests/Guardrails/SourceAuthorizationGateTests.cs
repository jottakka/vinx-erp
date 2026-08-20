using Vinx.Erp.AI;
using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

public sealed class SourceAuthorizationGateTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public void Same_company_source_is_authorized()
    {
        var gate = new SourceAuthorizationGate();
        var source = new AgentCitedSource("fonte-1", AlfaCompanyId);

        var result = gate.Authorize(Context(), [source]);

        var authorized = Assert.Single(result.AuthorizedSources);
        Assert.Equal(source, authorized);
        Assert.Empty(result.RemovedSources);
        Assert.False(result.CrossTenantViolation);
    }

    [Fact]
    public void Other_company_source_is_removed_and_flags_cross_tenant_violation()
    {
        var gate = new SourceAuthorizationGate();
        var intruder = new AgentCitedSource("fonte-beta", BetaCompanyId);

        var result = gate.Authorize(Context(), [intruder]);

        var removed = Assert.Single(result.RemovedSources);
        Assert.Equal(intruder, removed);
        Assert.Empty(result.AuthorizedSources);
        Assert.True(result.CrossTenantViolation);
    }

    [Fact]
    public void Empty_source_list_has_no_authorized_and_no_violation()
    {
        var gate = new SourceAuthorizationGate();

        var result = gate.Authorize(Context(), []);

        Assert.Empty(result.AuthorizedSources);
        Assert.Empty(result.RemovedSources);
        Assert.False(result.CrossTenantViolation);
    }

    private static AgentExecutionContext Context() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        AlfaCompanyId,
        "Alfa",
        Guid.NewGuid(),
        "resumo-diario",
        new HashSet<string>());
}
