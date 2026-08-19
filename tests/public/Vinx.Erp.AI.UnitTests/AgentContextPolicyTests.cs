using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentContextPolicyTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly Guid BetaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private static readonly Guid GammaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    [Fact]
    public void Authenticated_actor_single_company_receives_that_company_context()
    {
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var profile = ReadProfile();

        var resolution = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa")),
            new AgentContextRequest(actor, profile, AlfaCompanyId));

        Assert.Equal(AgentContextResolutionStatus.Resolved, resolution.Status);
        Assert.NotNull(resolution.Context);
        Assert.Equal(AlfaCompanyId, resolution.Context!.CompanyId);
        Assert.Contains(AgentCapabilities.Read, resolution.Context.Capabilities);
        Assert.Equal(actor.CorrelationId, resolution.Context.CorrelationId);
        Assert.Equal(profile.ProfileId, resolution.Context.ProfileId);
    }

    [Fact]
    public void Actor_with_multiple_companies_receives_only_requested_company()
    {
        var alfa = Membership(AlfaCompanyId, "Alfa");
        var beta = Membership(BetaCompanyId, "Beta");

        var resolution = AgentPolicy.EvaluateContext(
            Snapshot(alfa, beta),
            new AgentContextRequest(new ActorContext(Guid.NewGuid(), Guid.NewGuid()), ReadProfile(), AlfaCompanyId));

        Assert.Equal(AgentContextResolutionStatus.Resolved, resolution.Status);
        var context = Assert.IsType<AgentExecutionContext>(resolution.Context);
        Assert.Equal(alfa.CompanyId, context.CompanyId);
        Assert.Equal("Alfa", context.CompanyName);
        Assert.Equal(alfa.MembershipId, context.MembershipId);

        // Nenhuma menção a Beta em nenhuma propriedade do resultado (varredura completa do record).
        Assert.DoesNotContain("Beta", resolution.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(beta.CompanyId.ToString(), resolution.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(beta.MembershipId.ToString(), resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Company_scoped_journey_without_company_needs_clarification()
    {
        var resolution = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa")),
            new AgentContextRequest(new ActorContext(Guid.NewGuid(), Guid.NewGuid()), ReadProfile(), RequestedCompanyId: null));

        Assert.Equal(AgentContextResolutionStatus.NeedsClarification, resolution.Status);
        Assert.Equal("company_required", resolution.ReasonCode);
        Assert.Null(resolution.Context);
    }

    [Fact]
    public void Forged_company_fails_closed_without_authorized_data()
    {
        var resolution = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa")),
            new AgentContextRequest(new ActorContext(Guid.NewGuid(), Guid.NewGuid()), ReadProfile(), GammaCompanyId));

        Assert.Equal(AgentContextResolutionStatus.Denied, resolution.Status);
        Assert.Equal("company_not_authorized", resolution.ReasonCode);
        Assert.Null(resolution.Context);

        // A empresa forjada não aparece em lugar nenhum do resultado.
        Assert.DoesNotContain(GammaCompanyId.ToString(), resolution.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_capability_denies()
    {
        var resolution = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa", includeReadCapability: false)),
            new AgentContextRequest(new ActorContext(Guid.NewGuid(), Guid.NewGuid()), ReadProfile(), AlfaCompanyId));

        Assert.Equal(AgentContextResolutionStatus.Denied, resolution.Status);
        Assert.Equal("capability_denied", resolution.ReasonCode);
        Assert.Null(resolution.Context);
    }

    [Fact]
    public void Revoked_membership_not_reused()
    {
        // O resolver real é selado sobre o FoundationDbContext; a sequência abaixo
        // reproduz duas resoluções consecutivas de snapshot (1ª com Alfa, 2ª após
        // revogação) — a política consome cada snapshot fresco, sem cache.
        var actor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var request = new AgentContextRequest(actor, ReadProfile(), AlfaCompanyId);

        var granted = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa"), Membership(BetaCompanyId, "Beta")),
            request);
        Assert.Equal(AgentContextResolutionStatus.Resolved, granted.Status);
        Assert.NotNull(granted.Context);

        var revoked = AgentPolicy.EvaluateContext(
            Snapshot(Membership(BetaCompanyId, "Beta")),
            request);
        Assert.Equal(AgentContextResolutionStatus.Denied, revoked.Status);
        Assert.Equal("company_not_authorized", revoked.ReasonCode);
        Assert.Null(revoked.Context);
    }

    [Fact]
    public void Correlation_id_flows_from_actor()
    {
        var firstActor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var secondActor = new ActorContext(Guid.NewGuid(), Guid.NewGuid());
        var profile = ReadProfile();

        var first = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa")),
            new AgentContextRequest(firstActor, profile, AlfaCompanyId));
        var second = AgentPolicy.EvaluateContext(
            Snapshot(Membership(AlfaCompanyId, "Alfa")),
            new AgentContextRequest(secondActor, profile, AlfaCompanyId));

        Assert.Equal(firstActor.CorrelationId, first.Context!.CorrelationId);
        Assert.Equal(secondActor.CorrelationId, second.Context!.CorrelationId);
        Assert.NotEqual(first.Context.CorrelationId, second.Context.CorrelationId);
        Assert.Equal(firstActor.UserId, first.Context.ActorId);
        Assert.Equal(secondActor.UserId, second.Context.ActorId);
    }

    [Fact]
    public void ValidateRequest_returns_reason_for_empty_message_and_unknown_profile()
    {
        var profile = ReadProfile();

        Assert.Equal("message_empty", AgentPolicy.ValidateRequest("  ", profile));
        Assert.Equal("message_empty", AgentPolicy.ValidateRequest("", profile));
        Assert.Equal("profile_unknown", AgentPolicy.ValidateRequest("resuma X", null));
        Assert.Null(AgentPolicy.ValidateRequest("resuma X", profile));
    }

    private static AgentTaskProfile ReadProfile()
    {
        Assert.True(AgentTaskProfileCatalog.TryGet(AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, out var profile));
        return profile!;
    }

    private static ActorAuthorizationSnapshot Snapshot(params MembershipCapabilitySummary[] companies) => new(companies);

    private static MembershipCapabilitySummary Membership(
        Guid companyId,
        string companyName,
        bool includeReadCapability = true)
    {
        var capabilities = includeReadCapability
            ? new[] { ReadCapability() }
            : Array.Empty<EffectiveCapability>();
        return new MembershipCapabilitySummary(Guid.NewGuid(), companyId, companyName, capabilities);
    }

    private static EffectiveCapability ReadCapability() =>
        new(AgentCapabilities.Read, new HashSet<CapabilityScope> { CapabilityScope.Company });
}
