using Vinx.Erp.AI.Guardrails;
using Xunit;

namespace Vinx.Erp.AI.UnitTests.Guardrails;

// Fixtures sintéticas: domínios sob .example (reservado pela RFC 2606) e
// identificadores publicados como exemplo em documentação pública; nenhum
// dado pessoal real aparece ou é produzido aqui.
public sealed class SensitiveOutputRedactorTests
{
    private static SensitiveOutputRedactor Redactor(params string[] allowedHosts) =>
        new(new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Email_is_replaced_by_fixed_token_without_echoing_address()
    {
        var result = Redactor().Redact("Enviei o pedido para joao.silva@exemplo.com ontem");

        Assert.Equal("Enviei o pedido para [email-redigido] ontem", result.RedactedText);
        Assert.True(result.RedactedSomething);
        Assert.DoesNotContain("joao.silva", result.RedactedText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatted_cpf_is_redacted()
    {
        var result = Redactor().Redact("CPF 123.456.789-09 aprovado no crédito");

        Assert.Equal("CPF [identificador-redigido] aprovado no crédito", result.RedactedText);
        Assert.True(result.RedactedSomething);
    }

    [Fact]
    public void Raw_cpf_is_redacted_without_digit_echo()
    {
        var result = Redactor().Redact("Cliente 12345678909 liberado");

        Assert.Equal("Cliente [identificador-redigido] liberado", result.RedactedText);
        Assert.DoesNotContain("123456", result.RedactedText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatted_cnpj_is_redacted()
    {
        var result = Redactor().Redact("Emitente 12.345.678/0001-95 no XML");

        Assert.Equal("Emitente [identificador-redigido] no XML", result.RedactedText);
    }

    [Fact]
    public void Raw_cnpj_becomes_one_identifier_token_not_two_cpf_tokens()
    {
        var result = Redactor().Redact("CNPJ 12345678000195 consultado");

        // Sequência de 14 dígitos é um único identificador: nunca dois de 11.
        Assert.Equal("CNPJ [identificador-redigido] consultado", result.RedactedText);
        Assert.Equal(1, CountOccurrences(result.RedactedText!, "[identificador-redigido]"));
        Assert.DoesNotContain("12345678", result.RedactedText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bank_agency_with_context_prefix_is_redacted()
    {
        var result = Redactor().Redact("Pagamento na Agência 1234-5 hoje");

        Assert.Equal("Pagamento na Agência [dado-bancario-redigido] hoje", result.RedactedText);
    }

    [Fact]
    public void Bank_account_with_pt_and_en_context_prefix_is_redacted()
    {
        var pt = Redactor().Redact("Deposite na conta: 54321-6 até sexta");
        var en = Redactor().Redact("Transfer to account 1234567-8 today");

        Assert.Equal("Deposite na conta [dado-bancario-redigido] até sexta", pt.RedactedText);
        Assert.Equal("Transfer to account [dado-bancario-redigido] today", en.RedactedText);
    }

    [Fact]
    public void Iban_like_code_is_redacted()
    {
        var result = Redactor().Redact("IBAN XX00TEST000000012345 no arquivo");

        Assert.Equal("IBAN [dado-bancario-redigido] no arquivo", result.RedactedText);
    }

    [Fact]
    public void Medical_markers_from_closed_list_are_redacted()
    {
        var result = Redactor().Redact("O diagnóstico: pneumonia consta; prescrição e CID-10 J18.9 anexos, dosagem 500 mg");

        Assert.DoesNotContain("diagnóstico", result.RedactedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("prescrição", result.RedactedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("CID-10", result.RedactedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("500 mg", result.RedactedText!, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(result.RedactedText!, "[conteudo-medico-redigido]"));
    }

    [Fact]
    public void Url_with_allowed_host_stays_intact_including_digits_in_path()
    {
        var text = "Veja https://docs.vinx.example/guia/pedido/12345678901 para detalhes";
        var result = Redactor("docs.vinx.example").Redact(text);

        Assert.Equal(text, result.RedactedText);
        Assert.False(result.RedactedSomething);
    }

    [Fact]
    public void Url_host_allow_list_is_case_insensitive()
    {
        var text = "Guia em https://DOCS.VINX.EXAMPLE/interno";
        var result = Redactor("docs.vinx.example").Redact(text);

        Assert.Equal(text, result.RedactedText);
        Assert.False(result.RedactedSomething);
    }

    [Fact]
    public void Url_with_unknown_host_is_removed()
    {
        var mid = Redactor("docs.vinx.example").Redact("Detalhes em https://malicioso.example/rota?x=1 fim");

        Assert.Equal("Detalhes em [link-removido] fim", mid.RedactedText);
        Assert.True(mid.RedactedSomething);
    }

    [Fact]
    public void Url_at_sentence_end_keeps_trailing_punctuation()
    {
        var result = Redactor().Redact("Acesse https://malicioso.example/x.");

        Assert.Equal("Acesse [link-removido].", result.RedactedText);
    }

    [Fact]
    public void Clean_text_returns_unchanged_without_redaction_flag()
    {
        const string text = "Resumo do dia: pedido 123 aprovado em 2 minutos.";

        var result = Redactor("docs.vinx.example").Redact(text);

        Assert.Equal(text, result.RedactedText);
        Assert.False(result.RedactedSomething);
    }

    [Fact]
    public void Null_and_empty_text_pass_through_without_redaction()
    {
        Assert.Null(Redactor().Redact(null).RedactedText);
        Assert.False(Redactor().Redact(null).RedactedSomething);
        Assert.Empty(Redactor().Redact(string.Empty).RedactedText!);
        Assert.False(Redactor().Redact(string.Empty).RedactedSomething);
    }

    [Fact]
    public void Mixed_sensitive_text_redacts_every_category_without_any_echo()
    {
        var result = Redactor("docs.vinx.example").Redact(
            "Fale com joao@exemplo.com, CPF 123.456.789-09, conta 54321-6, dosagem 500 mg e https://malicioso.example/x.");

        var redacted = result.RedactedText!;
        Assert.Contains("[email-redigido]", redacted, StringComparison.Ordinal);
        Assert.Contains("[identificador-redigido]", redacted, StringComparison.Ordinal);
        Assert.Contains("[dado-bancario-redigido]", redacted, StringComparison.Ordinal);
        Assert.Contains("[conteudo-medico-redigido]", redacted, StringComparison.Ordinal);
        Assert.Contains("[link-removido]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("joao@", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("123.456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("54321", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("500 mg", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("malicioso", redacted, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
