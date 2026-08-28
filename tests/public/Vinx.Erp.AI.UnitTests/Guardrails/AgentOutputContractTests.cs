using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

public sealed class AgentOutputContractTests
{
    [Fact]
    public void Valid_payload_with_required_fields_passes()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo", "corpo" });

        var validation = contract.Validate("""{ "titulo": "R", "corpo": "C" }""");

        Assert.True(validation.IsValid);
        Assert.Equal("contract_valid", validation.ReasonCode);
    }

    [Fact]
    public void Missing_required_field_reports_field_name()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo", "corpo" });

        var validation = contract.Validate("""{ "titulo": "R" }""");

        Assert.False(validation.IsValid);
        Assert.Equal("required_field_missing:corpo", validation.ReasonCode);
    }

    [Fact]
    public void Unknown_field_reports_field_name()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo" });

        var validation = contract.Validate("""{ "titulo": "R", "extra": 1 }""");

        Assert.False(validation.IsValid);
        Assert.Equal("unknown_field:extra", validation.ReasonCode);
    }

    [Fact]
    public void Malformed_or_non_object_json_fails_closed()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo" });

        // JSON quebrado, array e escalar: nenhum é objeto raiz válido.
        Assert.False(contract.Validate("{ nope").IsValid);
        Assert.Equal("structured_json_invalid", contract.Validate("{ nope").ReasonCode);
        Assert.Equal("structured_json_invalid", contract.Validate("""["titulo"]""").ReasonCode);
        Assert.Equal("structured_json_invalid", contract.Validate("42").ReasonCode);
    }

    [Fact]
    public void Null_structured_json_with_required_fields_fails_closed()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo" });

        var validation = contract.Validate(null);

        Assert.False(validation.IsValid);
        Assert.Equal("structured_json_invalid", validation.ReasonCode);
    }

    [Fact]
    public void Null_structured_json_without_required_fields_is_valid()
    {
        var contract = new AgentOutputContract("conversa-livre", new HashSet<string>());

        var validation = contract.Validate(null);

        Assert.True(validation.IsValid);
        Assert.Equal("contract_valid", validation.ReasonCode);
    }

    [Fact]
    public void Duplicate_required_field_fails_closed()
    {
        var contract = new AgentOutputContract("resumo-diario", new HashSet<string> { "titulo" });

        var validation = contract.Validate("""{ "titulo": "R1", "titulo": "R2" }""");

        Assert.False(validation.IsValid);
        Assert.Equal("structured_json_invalid", validation.ReasonCode);
    }
}
