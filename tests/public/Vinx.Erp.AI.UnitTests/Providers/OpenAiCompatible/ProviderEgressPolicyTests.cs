using System.Net;
using Vinx.Erp.AI.Providers.OpenAiCompatible;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Providers.OpenAiCompatible;

public sealed class ProviderEgressPolicyTests
{
    private static IReadOnlyList<string> Validate(
        string endpoint,
        IReadOnlyList<string>? approvedHosts = null,
        int? approvedPort = null) =>
        ProviderEgressPolicy.ValidateEndpoint(
            new Uri(endpoint, UriKind.Absolute), approvedHosts, approvedPort);

    [Theory]
    [InlineData("https://provider.example.com/v1/")]
    [InlineData("https://api.deepseek.com/v1/")]
    [InlineData("https://8.8.8.8/v1/")]
    [InlineData("https://[2606:4700::1111]/v1/")]
    public void Public_https_endpoints_pass(string endpoint)
    {
        Assert.Empty(Validate(endpoint));
    }

    [Theory]
    [InlineData("http://provider.example.com/v1/")]
    public void Clear_text_endpoints_are_rejected(string endpoint)
    {
        Assert.Contains(
            "endpoint must be absolute https",
            string.Join(';', Validate(endpoint)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost/v1/")]
    [InlineData("https://LocalHost/v1/")]
    [InlineData("https://localhost./v1/")]
    public void Localhost_endpoints_are_rejected(string endpoint)
    {
        Assert.Contains("localhost", string.Join(';', Validate(endpoint)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1/v1/")]
    [InlineData("https://10.1.2.3/v1/")]
    [InlineData("https://172.16.0.1/v1/")]
    [InlineData("https://172.31.255.255/v1/")]
    [InlineData("https://192.168.1.1/v1/")]
    [InlineData("https://169.254.169.254/v1/")]
    [InlineData("https://100.64.0.1/v1/")]
    [InlineData("https://192.0.2.5/v1/")]
    [InlineData("https://198.51.100.7/v1/")]
    [InlineData("https://203.0.113.9/v1/")]
    [InlineData("https://198.18.0.1/v1/")]
    [InlineData("https://224.0.0.1/v1/")]
    [InlineData("https://240.1.2.3/v1/")]
    [InlineData("https://255.255.255.255/v1/")]
    [InlineData("https://0.0.0.0/v1/")]
    [InlineData("https://[::1]/v1/")]
    [InlineData("https://[::]/v1/")]
    [InlineData("https://[fc00::1]/v1/")]
    [InlineData("https://[fd12::1]/v1/")]
    [InlineData("https://[fe80::1]/v1/")]
    [InlineData("https://[ff02::1]/v1/")]
    [InlineData("https://[2001:db8::1]/v1/")]
    [InlineData("https://[::ffff:10.0.0.1]/v1/")]
    public void Prohibited_address_ranges_are_rejected(string endpoint)
    {
        Assert.Contains(
            Validate(endpoint),
            failure => failure.Contains("prohibited", StringComparison.Ordinal)
                || failure.Contains("localhost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://0x7f000001/v1/")]
    [InlineData("https://0177.0.0.1/v1/")]
    [InlineData("https://10.1/v1/")]
    [InlineData("https://12345/v1/")]
    public void Non_standard_numeric_ip_spellings_resolve_to_prohibited_ranges(string endpoint)
    {
        // O parser do .NET aceita hex/octal/quad parcial estilo inet_aton:
        // 0x7f000001 e 0177.0.0.1 são 127.0.0.1, 10.1 é 10.0.0.1 e 12345 é
        // 0.0.48.57 — todas caem na faixa proibida pela validação de literal.
        var failures = string.Join(';', Validate(endpoint));
        Assert.Contains("prohibited range", failures, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://999.999.999.999/v1/")]
    [InlineData("https://1.2.3.4.5/v1/")]
    public void Unparseable_numeric_hosts_are_rejected_as_non_standard_spellings(string endpoint)
    {
        Assert.Contains(
            "numeric IP spelling",
            string.Join(';', Validate(endpoint)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Null_or_relative_endpoint_is_rejected()
    {
        Assert.Contains(
            "endpoint must be absolute https",
            ProviderEgressPolicy.ValidateEndpoint(null),
            StringComparer.Ordinal);
        Assert.Contains(
            "endpoint must be absolute https",
            ProviderEgressPolicy.ValidateEndpoint(new Uri("/v1/", UriKind.Relative)),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Non_standard_port_is_rejected_unless_explicitly_approved()
    {
        Assert.Contains(
            "port must be 443",
            string.Join(';', Validate("https://provider.example.com:8443/v1/")),
            StringComparison.Ordinal);

        Assert.Empty(Validate("https://provider.example.com:8443/v1/", approvedPort: 8443));
    }

    [Fact]
    public void Approved_hosts_match_exact_and_dot_boundary_suffix_only()
    {
        var approved = new[] { "api.deepseek.com" };

        Assert.Empty(Validate("https://api.deepseek.com/v1/", approved));
        Assert.Empty(Validate("https://eu.api.deepseek.com/v1/", approved));
        Assert.Contains(
            "approved host list",
            string.Join(';', Validate("https://notdeepseek.com/v1/", approved)),
            StringComparison.Ordinal);
        // Sufixo embutido em outro domínio não é subdomínio: fronteira de ponto.
        Assert.Contains(
            "approved host list",
            string.Join(';', Validate("https://api.deepseek.com.evil.example/v1/", approved)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolved_addresses_are_validated_per_address()
    {
        Assert.Empty(ProviderEgressPolicy.ValidateResolvedAddresses(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111")]));
        Assert.Contains(
            "resolved address is in a prohibited range",
            ProviderEgressPolicy.ValidateResolvedAddresses(
                [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5")]),
            StringComparer.Ordinal);
        Assert.Empty(ProviderEgressPolicy.ValidateResolvedAddresses([]));
    }
}
