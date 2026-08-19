using Microsoft.Extensions.Options;

namespace Vinx.Erp.AI.Providers.DeepSeek;

/// <summary>
/// Perfil server-side aprovado para chamadas DeepSeek v4 Flash. Endpoint,
/// modelo, timeout, orçamento e limites vêm sempre da configuração aprovada;
/// o request do chamador nunca escolhe esses valores.
/// </summary>
public sealed class DeepSeekOptions
{
    /// <summary>Endpoint OpenAI-compatible oficial do DeepSeek.</summary>
    public static readonly Uri ApprovedEndpoint = new("https://api.deepseek.com/v1/", UriKind.Absolute);

    public Uri Endpoint { get; set; } = ApprovedEndpoint;

    /// <summary>Chave vinda do cofre de configuração; nunca é logada ou devolvida.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "deepseek-v4-flash";

    /// <summary>Latência máxima por chamada; vencida, a execução falha como timeout sem retry.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    public int MaxMessages { get; set; } = 32;

    public int MaxInputCharacters { get; set; } = 96_000;

    public int MaxOutputTokens { get; set; } = 2_048;

    /// <summary>Orçamento por execução: teto de tokens de entrada+saida reportado para o ledger do runtime.</summary>
    public int MaxTotalTokensPerCall { get; set; } = 16_384;

    /// <summary>
    /// Valida o perfil no startup (options pattern + ValidateOnStart). As
    /// mensagens de falha nunca ecoam o valor da chave.
    /// </summary>
    public sealed class Validator : IValidateOptions<DeepSeekOptions>
    {
        private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(5);

        public ValidateOptionsResult Validate(string? name, DeepSeekOptions? options)
        {
            if (options is null)
            {
                return ValidateOptionsResult.Fail("DeepSeek profile is required.");
            }

            var failures = new List<string>();

            if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != "https")
            {
                failures.Add("endpoint must be absolute https");
            }

            if (string.IsNullOrWhiteSpace(options.Model))
            {
                failures.Add("model must be a non-empty approved model name");
            }

            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                // A chave nunca aparece na mensagem.
                failures.Add("api key must be provided by the approved configuration");
            }

            if (options.Timeout < MinimumTimeout || options.Timeout > MaximumTimeout)
            {
                failures.Add($"timeout must be between {MinimumTimeout.TotalSeconds:N0}s and {MaximumTimeout.TotalMinutes:N0}m");
            }

            if (options.MaxMessages is < 1 or > 256)
            {
                failures.Add("message limit must be between 1 and 256");
            }

            if (options.MaxInputCharacters is < 1 or > 1_000_000)
            {
                failures.Add("input character limit must be between 1 and 1000000");
            }

            if (options.MaxOutputTokens is < 1 or > 32_768)
            {
                failures.Add("output token limit must be between 1 and 32768");
            }

            if (options.MaxTotalTokensPerCall is < 1 or > 200_000
                || options.MaxTotalTokensPerCall < options.MaxOutputTokens)
            {
                failures.Add("budget per call must cover the output token limit");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}
