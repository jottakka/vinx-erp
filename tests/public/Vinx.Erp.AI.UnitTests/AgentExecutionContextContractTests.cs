using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentExecutionContextContractTests
{
    private static readonly Guid AlfaCompanyId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void Context_exposes_only_safe_properties()
    {
        // Allowlist exata: qualquer campo novo (prompt, JWT, e-mail, segredo) quebra o teste.
        var expected = new[]
        {
            nameof(AgentExecutionContext.ActorId),
            nameof(AgentExecutionContext.CorrelationId),
            nameof(AgentExecutionContext.CompanyId),
            nameof(AgentExecutionContext.CompanyName),
            nameof(AgentExecutionContext.MembershipId),
            nameof(AgentExecutionContext.ProfileId),
            nameof(AgentExecutionContext.Capabilities),
        };

        var actual = typeof(AgentExecutionContext)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name), actual);
    }

    [Fact]
    public void Context_default_tostring_contains_no_message_or_secret()
    {
        const string sensitiveMessage = "mensagem-super-secreta-do-usuario";
        const string secretToken = "jwt-eyJhbGciOi.secret-do-teste";

        // A única API que toca a mensagem é a validação; o contexto é construído
        // a partir do snapshot e jamais recebe prompt ou credencial.
        Assert.Null(AgentPolicy.ValidateRequest(sensitiveMessage, ReadProfile()));

        var context = ResolveContext();

        var text = context.ToString();
        Assert.DoesNotContain(sensitiveMessage, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_names_are_minimal_strings()
    {
        // Capabilities é um conjunto de nomes normativos: sem escopos, ids ou extras.
        Assert.Equal(
            typeof(IReadOnlySet<string>),
            typeof(AgentExecutionContext).GetProperty(nameof(AgentExecutionContext.Capabilities))!.PropertyType);

        var membership = new MembershipCapabilitySummary(
            Guid.NewGuid(),
            AlfaCompanyId,
            "Alfa",
            new[]
            {
                new EffectiveCapability(
                    AgentCapabilities.Read,
                    new HashSet<CapabilityScope> { CapabilityScope.Company }),
                new EffectiveCapability(
                    "reports.export",
                    new HashSet<CapabilityScope> { CapabilityScope.Self, CapabilityScope.Company }),
            });

        var context = ResolveContext(membership);

        Assert.Equal(new[] { AgentCapabilities.Read, "reports.export" }, context.Capabilities.Order());
    }

    private static AgentExecutionContext ResolveContext(MembershipCapabilitySummary? membership = null)
    {
        var resolution = AgentPolicy.EvaluateContext(
            new ActorAuthorizationSnapshot(new[] { membership ?? AlfaMembership() }),
            new AgentContextRequest(
                new ActorContext(Guid.NewGuid(), Guid.NewGuid()),
                ReadProfile(),
                AlfaCompanyId));

        Assert.Equal(AgentContextResolutionStatus.Resolved, resolution.Status);
        return resolution.Context!;
    }

    private static MembershipCapabilitySummary AlfaMembership() =>
        new(
            Guid.NewGuid(),
            AlfaCompanyId,
            "Alfa",
            new[]
            {
                new EffectiveCapability(
                    AgentCapabilities.Read,
                    new HashSet<CapabilityScope> { CapabilityScope.Company }),
            });

    private static AgentTaskProfile ReadProfile()
    {
        Assert.True(AgentTaskProfileCatalog.TryGet(AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, out var profile));
        return profile!;
    }
}
