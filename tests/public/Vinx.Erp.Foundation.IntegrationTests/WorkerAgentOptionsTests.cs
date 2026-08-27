extern alias workerhost;

using WorkerAgentRunOptions = workerhost::WorkerAgentRunOptions;
using WorkerAgentVisionOptions = workerhost::WorkerAgentVisionOptions;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class WorkerAgentOptionsTests
{
    [Fact]
    public void Main_and_vision_accept_only_their_exact_normalized_hosts()
    {
        var options = ValidMain();
        options.Endpoint = new Uri("https://AGENT-PROVIDER.EXAMPLE/v1");
        options.ApprovedHosts = [" agent-provider.example. "];
        options.Vision = ValidVision();
        options.Vision.Endpoint = new Uri("https://VISION-PROVIDER.EXAMPLE/v1");
        options.Vision.ApprovedHosts = [" vision-provider.example. "];

        options.Validate(ReadCredential);
    }

    [Theory]
    [MemberData(nameof(InvalidMainHostProfiles))]
    public void Main_rejects_missing_parent_or_unrelated_IP_hosts(
        string endpoint,
        string[] approvedHosts)
    {
        var options = ValidMain();
        options.Endpoint = new Uri(endpoint);
        options.ApprovedHosts = approvedHosts;

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(ReadCredential));

        Assert.Equal("Agent run configuration is invalid.", exception.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidVisionHostProfiles))]
    public void Vision_rejects_missing_parent_or_unrelated_IP_hosts(
        string endpoint,
        string[] approvedHosts)
    {
        var options = new WorkerAgentRunOptions
        {
            Vision = ValidVision(),
        };
        options.Vision.Endpoint = new Uri(endpoint);
        options.Vision.ApprovedHosts = approvedHosts;

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate(ReadCredential));

        Assert.Equal("Agent run configuration is invalid.", exception.Message);
    }

    public static TheoryData<string, string[]> InvalidMainHostProfiles => new()
    {
        { "https://agent-provider.example/v1", [] },
        { "https://agent-provider.example/v1", [" "] },
        { "https://api.agent-provider.example/v1", ["agent-provider.example"] },
        { "https://8.8.8.8/v1", ["1.1.1.1"] },
    };

    public static TheoryData<string, string[]> InvalidVisionHostProfiles => new()
    {
        { "https://vision-provider.example/v1", [] },
        { "https://vision-provider.example/v1", [" "] },
        { "https://api.vision-provider.example/v1", ["vision-provider.example"] },
        { "https://8.8.8.8/v1", ["1.1.1.1"] },
    };

    private static WorkerAgentRunOptions ValidMain() => new()
    {
        Provider = "openai_compatible",
        Endpoint = new Uri("https://agent-provider.example/v1"),
        Model = "approved-model",
        ApprovedHosts = ["agent-provider.example"],
    };

    private static WorkerAgentVisionOptions ValidVision() => new()
    {
        Provider = "openai_compatible",
        Endpoint = new Uri("https://vision-provider.example/v1"),
        Model = "approved-vision-model",
        ApprovedHosts = ["vision-provider.example"],
        AllowedClassifications = ["agentic_attachment"],
    };

    private static string? ReadCredential(string name) =>
        name == "VINX_AGENT_PROVIDER_API_KEY" ? "credential-placeholder" : null;
}
