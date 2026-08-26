namespace Vinx.Erp.Notifications.Providers.Email;

/// <summary>
/// Configuração do adapter SMTP do canal e-mail. Segredos (senha) vêm
/// exclusivamente da configuração/env e nunca aparecem em log, argumento ou
/// resposta. Todo o conjunto é opcional no boot do worker: sem SMTP configurado
/// o worker sobe atendendo filas independentes e o provider falha fechado por
/// entrega, sem conexão externa e sem confirmação falsa.
/// </summary>
public sealed class EmailProviderOptions
{
    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_HOST")]
    public string Host { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_PORT")]
    public int Port { get; set; } = 587;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_USE_TLS")]
    public bool UseTls { get; set; } = true;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_USERNAME")]
    public string Username { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_PASSWORD")]
    public string Password { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_FROM_ADDRESS")]
    public string FromAddress { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_FROM_NAME")]
    public string FromName { get; set; } = string.Empty;

    [Microsoft.Extensions.Configuration.ConfigurationKeyName("SMTP_EMAIL_TIMEOUT_SECONDS")]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Conjunto completo e válido habilita o envio; qualquer ausência parcial é
    /// erro de deploy (validado on-start) e o provider falha fechado.
    /// </summary>
    public bool OutboundIsConfigured =>
        !string.IsNullOrWhiteSpace(Host)
        && Port is > 0 and <= 65535
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && EmailAddressPolicy.IsAllowedEmail(FromAddress)
        && TimeoutSeconds > 0;
}

/// <summary>
/// Validação pura do endereço de e-mail sem I/O de rede.
/// </summary>
public static class EmailAddressPolicy
{
    /// <summary>
    /// Formato "destinatario@dominio", domínio com ponto e sem espaços. A
    /// política canônica de DNS/SSRF não se aplica ao SMTP: o destino é fixo
    /// por configuração do operador, não controlado por terceiros ou cliente.
    /// </summary>
    public static bool IsAllowedEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320)
        {
            return false;
        }

        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1 || value.IndexOf('@', at + 1) >= 0)
        {
            return false;
        }

        var domain = value[(at + 1)..];
        return !value.Any(char.IsWhiteSpace)
            && domain.Contains('.')
            && !domain.StartsWith('.')
            && !domain.EndsWith('.');
    }
}
