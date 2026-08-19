using Vinx.Erp.AI;
using Xunit;

namespace Vinx.Erp.AI.UnitTests;

public sealed class AgentTaskProfileTests
{
    private static readonly TimeSpan ApprovedTimeout = TimeSpan.FromSeconds(30);

    private const int ApprovedTurnLimit = 1;

    private const int ApprovedBudget = 8_000;

    [Fact]
    public void CreateValidated_accepts_read_only_summarize_v1_values()
    {
        var profile = AgentTaskProfile.CreateValidated(
            AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            "summarize",
            AgentJourneyScope.Company,
            AgentCapabilities.Read,
            modelSelection: null,
            timeout: ApprovedTimeout,
            turnLimit: ApprovedTurnLimit,
            maxTotalTokens: ApprovedBudget,
            allowedTools: Array.Empty<string>());

        Assert.Equal(AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, profile.ProfileId);
        Assert.Equal("summarize", profile.Journey);
        Assert.Equal(AgentJourneyScope.Company, profile.Scope);
        Assert.Equal(AgentCapabilities.Read, profile.RequiredCapabilityName);
        Assert.Null(profile.ModelSelection);
        Assert.Equal(ApprovedTimeout, profile.Timeout);
        Assert.Equal(ApprovedTurnLimit, profile.TurnLimit);
        Assert.Equal(ApprovedBudget, profile.MaxTotalTokens);
        Assert.Empty(profile.AllowedTools);
    }

    [Theory]
    [InlineData("", "summarize", "ai.read", nameof(AgentTaskProfile.ProfileId))]
    [InlineData("  ", "summarize", "ai.read", nameof(AgentTaskProfile.ProfileId))]
    [InlineData("read_only_summarize_v1", "", "ai.read", nameof(AgentTaskProfile.Journey))]
    [InlineData("read_only_summarize_v1", "  ", "ai.read", nameof(AgentTaskProfile.Journey))]
    [InlineData("read_only_summarize_v1", "summarize", "", nameof(AgentTaskProfile.RequiredCapabilityName))]
    [InlineData("read_only_summarize_v1", "summarize", "  ", nameof(AgentTaskProfile.RequiredCapabilityName))]
    public void Validate_rejects_empty_profile_id_journey_and_capability(
        string profileId,
        string journey,
        string requiredCapabilityName,
        string expectedParamName)
    {
        AssertProfileRejected(
            BuildProfile(profileId: profileId, journey: journey, requiredCapabilityName: requiredCapabilityName),
            expectedParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Validate_rejects_non_positive_timeout(int timeoutSeconds)
    {
        AssertProfileRejected(
            BuildProfile(timeout: TimeSpan.FromSeconds(timeoutSeconds)),
            nameof(AgentTaskProfile.Timeout));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Validate_rejects_turn_limit_above_one_or_below_one(int turnLimit)
    {
        AssertProfileRejected(
            BuildProfile(turnLimit: turnLimit),
            nameof(AgentTaskProfile.TurnLimit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8_000)]
    public void Validate_rejects_non_positive_budget(int maxTotalTokens)
    {
        AssertProfileRejected(
            BuildProfile(maxTotalTokens: maxTotalTokens),
            nameof(AgentTaskProfile.MaxTotalTokens));
    }

    [Fact]
    public void Validate_rejects_company_scope_without_company_scoped_requirement()
    {
        // Company é o único valor definido no enum: não existe journey aprovado fora do contexto de empresa.
        Assert.True(Enum.IsDefined<AgentJourneyScope>(AgentJourneyScope.Company));
        Assert.Equal(new[] { nameof(AgentJourneyScope.Company) }, Enum.GetNames<AgentJourneyScope>());

        BuildProfile().Validate();

        AssertProfileRejected(
            BuildProfile(scope: (AgentJourneyScope)42),
            nameof(AgentTaskProfile.Scope));
    }

    [Fact]
    public void Catalog_returns_approved_profile_and_rejects_unknown()
    {
        Assert.True(AgentTaskProfileCatalog.TryGet(AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, out var profile));
        Assert.NotNull(profile);
        Assert.Equal("summarize", profile!.Journey);
        Assert.Equal(AgentJourneyScope.Company, profile.Scope);
        Assert.Equal(AgentCapabilities.Read, profile.RequiredCapabilityName);

        Assert.False(AgentTaskProfileCatalog.TryGet("inventado", out var unknown));
        Assert.Null(unknown);

        Assert.False(AgentTaskProfileCatalog.TryGet(" ", out var whitespace));
        Assert.Null(whitespace);
    }

    [Fact]
    public void Catalog_profile_is_read_only()
    {
        // Trava de regressão: mudança de política (ferramentas ou loop) exige revisão explícita.
        Assert.True(AgentTaskProfileCatalog.TryGet(AgentTaskProfileCatalog.ReadOnlySummarizeProfileId, out var profile));

        Assert.Empty(profile!.AllowedTools);
        Assert.Equal(ApprovedTurnLimit, profile.TurnLimit);
    }

    [Fact]
    public void Client_cannot_inject_profile_values()
    {
        // Records posicionais expõem setters init-only (modreq IsExternalInit): nenhuma propriedade é atribuível após a construção.
        Assert.True(typeof(AgentTaskProfile).IsSealed);

        foreach (var property in typeof(AgentTaskProfile).GetProperties())
        {
            var setter = property.GetSetMethod();
            Assert.NotNull(setter);
            Assert.Contains(
                setter!.ReturnParameter.GetRequiredCustomModifiers(),
                modifier => modifier.Name == "IsExternalInit");
        }
    }

    private static AgentTaskProfile BuildProfile(
        string profileId = "read_only_summarize_v1",
        string journey = "summarize",
        AgentJourneyScope scope = AgentJourneyScope.Company,
        string requiredCapabilityName = AgentCapabilities.Read,
        AgentModelSelection? modelSelection = null,
        TimeSpan? timeout = null,
        int turnLimit = ApprovedTurnLimit,
        int maxTotalTokens = ApprovedBudget,
        IReadOnlyList<string>? allowedTools = null)
        => new(
            profileId,
            journey,
            scope,
            requiredCapabilityName,
            modelSelection,
            timeout ?? ApprovedTimeout,
            turnLimit,
            maxTotalTokens,
            allowedTools ?? Array.Empty<string>());

    private static void AssertProfileRejected(AgentTaskProfile profile, string expectedParamName)
    {
        var viaValidate = Assert.Throws<ArgumentException>(profile.Validate);
        Assert.Equal(expectedParamName, viaValidate.ParamName);

        var viaCreate = Assert.Throws<ArgumentException>(
            () => AgentTaskProfile.CreateValidated(
                profile.ProfileId,
                profile.Journey,
                profile.Scope,
                profile.RequiredCapabilityName,
                profile.ModelSelection,
                profile.Timeout,
                profile.TurnLimit,
                profile.MaxTotalTokens,
                profile.AllowedTools));
        Assert.Equal(expectedParamName, viaCreate.ParamName);
    }
}
