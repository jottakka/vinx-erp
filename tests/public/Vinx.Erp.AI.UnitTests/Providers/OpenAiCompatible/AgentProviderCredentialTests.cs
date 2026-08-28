using Xunit;
using Vinx.Erp.AI.Providers.OpenAiCompatible;

namespace Vinx.Erp.AI.UnitTests.Providers.OpenAiCompatible;

public sealed class AgentProviderCredentialTests
{
    [Fact]
    public void Resolver_accepts_only_the_dedicated_process_variable()
    {
        var credential = AgentProviderCredential.Resolve(
            name => name == AgentProviderCredential.EnvironmentVariable ? "synthetic-test-key" : "ignored");

        Assert.NotNull(credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("synthetic\nkey")]
    [InlineData("synthetic\rkey")]
    [InlineData("synthetic\u0001key")]
    public void Resolver_rejects_absent_blank_or_control_character_values(string? value)
    {
        var credential = AgentProviderCredential.Resolve(
            name => name == AgentProviderCredential.EnvironmentVariable ? value : null);

        Assert.Null(credential);
    }

    [Fact]
    public void Resolver_does_not_read_bindable_agent_run_key()
    {
        var credential = AgentProviderCredential.Resolve(
            name => name == "AgentRun__ApiKey" ? "ignored-test-key" : null);

        Assert.Null(credential);
    }
}
