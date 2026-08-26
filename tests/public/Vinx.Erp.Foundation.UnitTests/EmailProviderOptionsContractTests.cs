using Vinx.Erp.Notifications.Providers.Email;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class EmailProviderOptionsContractTests
{
    private static EmailProviderOptions Configured() =>
        new()
        {
            Host = "smtp.example.test",
            Port = 587,
            UseTls = true,
            Username = "noreply@example.test",
            Password = "unit-test-placeholder",
            FromAddress = "noreply@example.test",
            FromName = "Vinx ERP",
            TimeoutSeconds = 10,
        };

    [Fact]
    public void Configured_options_validate_as_outbound_enabled()
    {
        Assert.True(Configured().OutboundIsConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("smtp.example.test")] // sem usuário/senha/from
    [InlineData("noreply@example.test")]
    public void Partial_or_absent_options_are_not_outbound_enabled(string onlyField)
    {
        var options = new EmailProviderOptions();
        // Nenhum campo completo preenchido: nunca deve habilitar saída.
        Assert.False(options.OutboundIsConfigured);

        if (onlyField.Length > 0)
        {
            options.Host = onlyField;
            Assert.False(options.OutboundIsConfigured);
        }
    }

    [Fact]
    public void Invalid_from_address_blocks_outbound_even_when_configured()
    {
        var options = Configured();
        options.FromAddress = "not-an-email";
        Assert.False(options.OutboundIsConfigured);
    }

    [Fact]
    public void Invalid_port_blocks_outbound()
    {
        var options = Configured();
        options.Port = 0;
        Assert.False(options.OutboundIsConfigured);
    }

    [Fact]
    public void Non_positive_timeout_blocks_outbound()
    {
        var options = Configured();
        options.TimeoutSeconds = 0;
        Assert.False(options.OutboundIsConfigured);
    }

    [Fact]
    public void Email_address_policy_accepts_valid_addresses()
    {
        Assert.True(EmailAddressPolicy.IsAllowedEmail("noreply@example.test"));
        Assert.True(EmailAddressPolicy.IsAllowedEmail("a.b@sub.example.co"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@missing-local")]
    [InlineData("missing-domain@")]
    [InlineData("two@@signs")]
    [InlineData("with space@example.test")]
    [InlineData("local@nodot-domain")]
    [InlineData("local@.leading-dot.test")]
    public void Email_address_policy_rejects_invalid_addresses(string value)
    {
        Assert.False(EmailAddressPolicy.IsAllowedEmail(value));
    }
}
