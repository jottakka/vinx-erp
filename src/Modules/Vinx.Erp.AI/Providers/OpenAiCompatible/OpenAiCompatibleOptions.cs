using Microsoft.Extensions.Options;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Perfil rejeitado na construção do client: a configuração viola as regras
/// do perfil aprovado. Mensagem fixa, sem ecoar chave, endpoint ou valores.
/// </summary>
public sealed class ProviderProfileRejectedException(string message) : Exception(message);

/// <summary>
/// Perfil server-side aprovado para chamadas a qualquer endpoint
/// OpenAI-compatible. Endpoint, modelo, timeout, orçamento, limites e
/// modalidades aceitas vêm sempre da configuração aprovada; o request do
/// chamador nunca escolhe esses valores. A validação corre na construção do
/// client; o wiring de host (`AddOptions`/`ValidateOnStart`) pertence à
/// fatia da API (#138).
/// </summary>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>Endpoint OpenAI-compatible aprovado no cofre de configuração.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Chave vinda do cofre de configuração; nunca é logada ou devolvida.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Latência máxima por chamada; vencida, a execução falha como timeout sem retry.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    public int MaxMessages { get; set; } = 32;

    public int MaxInputCharacters { get; set; } = 96_000;

    public int MaxOutputTokens { get; set; } = 2_048;

    /// <summary>Orçamento por execução: teto de tokens de entrada+saída reportado para o ledger do runtime.</summary>
    public int MaxTotalTokensPerCall { get; set; } = 16_384;

    /// <summary>
    /// Perfil de visão: quando falso (padrão), qualquer conteúdo de imagem na
    /// conversa é rejeitado deterministicamente antes de HTTP.
    /// </summary>
    public bool AllowImageInputs { get; set; }

    public int MaxImagesPerRequest { get; set; } = 4;

    public long MaxImageBytesPerImage { get; set; } = 5 * 1024 * 1024;

    public long MaxImageBytesPerRequest { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Estimativa de admissão preflight por imagem: conta contra o orçamento
    /// da chamada antes de qualquer HTTP; o usage real é reportado depois.
    /// </summary>
    public int EstimatedTokensPerImage { get; set; } = 1_000;

    /// <summary>
    /// Allowlist opcional de hosts (exato ou sufixo com fronteira de ponto).
    /// Quando vazia, qualquer host público https fora das faixas proibidas é
    /// aceito; a lista amplia o endurecimento, nunca substitui as faixas.
    /// </summary>
    public IReadOnlyList<string> ApprovedHosts { get; set; } = [];

    /// <summary>Porta não padrão aprovada explicitamente; sem ela, somente 443.</summary>
    public int? ApprovedPort { get; set; }

    /// <summary>Rótulo do perfil usado no escopo de logging (ex.: `ai/deepseek`).</summary>
    public string ModuleTag { get; set; } = "ai/openai-compatible";

    /// <summary>
    /// Valida o perfil (options pattern). Declarado para o host registrar com
    /// `ValidateOnStart` na fatia da API; nesta fatia o client valida na
    /// construção. As mensagens de falha nunca ecoam o valor da chave.
    /// </summary>
    public sealed class Validator : IValidateOptions<OpenAiCompatibleOptions>
    {
        private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(5);

        public ValidateOptionsResult Validate(string? name, OpenAiCompatibleOptions? options)
        {
            if (options is null)
            {
                return ValidateOptionsResult.Fail("OpenAI-compatible profile is required.");
            }

            var failures = new List<string>();

            failures.AddRange(ProviderEgressPolicy.ValidateEndpoint(
                options.Endpoint, options.ApprovedHosts, options.ApprovedPort));

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

            if (options.ApprovedPort is < 1 or > 65_535)
            {
                failures.Add("approved port must be between 1 and 65535");
            }

            if (string.IsNullOrWhiteSpace(options.ModuleTag) || options.ModuleTag.Contains('\n') || options.ModuleTag.Contains('\r'))
            {
                failures.Add("module tag must be a non-empty single-line label");
            }

            if (options.AllowImageInputs)
            {
                if (options.MaxImagesPerRequest is < 1 or > 16)
                {
                    failures.Add("image count limit must be between 1 and 16");
                }

                if (options.MaxImageBytesPerImage is < 65_536 or > 20_971_520)
                {
                    failures.Add("per-image byte limit must be between 65536 and 20971520");
                }

                if (options.MaxImageBytesPerRequest < options.MaxImageBytesPerImage
                    || options.MaxImageBytesPerRequest > 104_857_600)
                {
                    failures.Add("aggregate image byte limit must cover the per-image limit");
                }

                if (options.EstimatedTokensPerImage is < 1 or > 10_000)
                {
                    failures.Add("estimated tokens per image must be between 1 and 10000");
                }

                if ((long)options.MaxImagesPerRequest * options.EstimatedTokensPerImage
                    > options.MaxTotalTokensPerCall - options.MaxOutputTokens)
                {
                    failures.Add("image admission estimate exceeds the per-call budget");
                }
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}
