using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Platform;

/// <summary>
/// Re-deriva a garantia MFA no uso do privilégio consultando a Admin API do
/// Supabase: o sujeito ainda possui pelo menos um fator verificado? Falha de
/// configuração, rede ou payload não verifica e o chamador nega (fail closed),
/// sem reter dados do provedor e sem registrar PII.
/// </summary>
public sealed class SupabaseAdminMfaAssuranceVerifier(
    IHttpClientFactory httpClientFactory,
    SupabaseAuthOptions authentication,
    SupabaseAdminOptions administration,
    MfaAssuranceCache cache,
    ILogger<SupabaseAdminMfaAssuranceVerifier> logger
) : IMfaAssuranceVerifier
{
    public const string HttpClientName = "supabase-admin-mfa";
    private const int MaximumResponseBytes = 32 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const string Operation = "mfa_assurance_verification";
    private const string Module = "identity";
    private const string EventTemplate = "MFA assurance {Operation} {Module} {Result} {Reason}";

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly string _issuer = (authentication ?? throw new ArgumentNullException(nameof(authentication))).NormalizedIssuer;
    private readonly string _serviceRoleKey = (administration ?? throw new ArgumentNullException(nameof(administration))).ServiceRoleKey;
    private readonly MfaAssuranceCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly ILogger<SupabaseAdminMfaAssuranceVerifier> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<MfaAssuranceVerificationResult> VerifyAsync(
        string subject,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(subject)
            || string.IsNullOrWhiteSpace(_issuer)
            || string.IsNullOrWhiteSpace(_serviceRoleKey)
        )
        {
            LogUnavailable("configuration_missing");
            return MfaAssuranceVerificationResult.Unavailable();
        }

        if (_cache.TryGet(subject, out var cached))
        {
            return new MfaAssuranceVerificationResult(cached);
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_issuer}/admin/users/{Uri.EscapeDataString(subject)}"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);

        // Com ResponseHeadersRead o Timeout do HttpClient cobre só até os
        // headers: o orçamento precisa valer para a troca inteira (headers +
        // corpo), senão um stall no corpo escapa como 500 (bloqueio de
        // revisão). Cancelamento do chamador continua propagando.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                bounded.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogUnavailable("provider_timeout");
            return MfaAssuranceVerificationResult.Unavailable();
        }
        catch (HttpRequestException)
        {
            LogUnavailable("provider_unreachable");
            return MfaAssuranceVerificationResult.Unavailable();
        }
        catch (IOException)
        {
            LogUnavailable("provider_io_failure");
            return MfaAssuranceVerificationResult.Unavailable();
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                // Sujeito inexistente no provedor é uma resposta definitiva:
                // nenhum fator verificado pode existir.
                return CacheAndReturn(subject, MfaAssuranceVerificationResult.VerifiedFactorAbsent());
            }

            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable("provider_status_unexpected");
                return MfaAssuranceVerificationResult.Unavailable();
            }

            byte[]? payload;
            try
            {
                payload = await ReadResponseAsync(response, bounded.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                LogUnavailable("provider_timeout");
                return MfaAssuranceVerificationResult.Unavailable();
            }
            catch (IOException)
            {
                LogUnavailable("provider_io_failure");
                return MfaAssuranceVerificationResult.Unavailable();
            }
            catch (HttpRequestException)
            {
                LogUnavailable("provider_unreachable");
                return MfaAssuranceVerificationResult.Unavailable();
            }

            if (payload is null)
            {
                LogUnavailable("provider_payload_too_large");
                return MfaAssuranceVerificationResult.Unavailable();
            }

            MfaAssuranceVerificationResult result;
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (
                    !HasRequiredString(document.RootElement, "id", out var returnedSubject)
                    || !string.Equals(returnedSubject, subject, StringComparison.Ordinal)
                )
                {
                    LogUnavailable("provider_subject_mismatch");
                    return MfaAssuranceVerificationResult.Unavailable();
                }

                result = HasVerifiedFactor(document.RootElement)
                    ? MfaAssuranceVerificationResult.VerifiedFactorPresent()
                    : MfaAssuranceVerificationResult.VerifiedFactorAbsent();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                LogUnavailable("provider_payload_invalid");
                return MfaAssuranceVerificationResult.Unavailable();
            }

            return CacheAndReturn(subject, result);
        }
    }

    private MfaAssuranceVerificationResult CacheAndReturn(
        string subject,
        MfaAssuranceVerificationResult result
    )
    {
        _cache.Set(subject, result.State);
        return result;
    }

    private static bool HasVerifiedFactor(JsonElement user)
    {
        if (
            user.ValueKind is not JsonValueKind.Object
            || !user.TryGetProperty("factors", out var factors)
            || factors.ValueKind is not JsonValueKind.Array
        )
        {
            return false;
        }

        foreach (var factor in factors.EnumerateArray())
        {
            if (
                factor.ValueKind is JsonValueKind.Object
                && HasRequiredString(factor, "id", out _)
                && HasRequiredString(factor, "factor_type", out _)
                && factor.TryGetProperty("status", out var status)
                && status.ValueKind is JsonValueKind.String
                && string.Equals(status.GetString(), "verified", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<byte[]?> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            return null;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool HasRequiredString(JsonElement objectElement, string name, out string value)
    {
        value = string.Empty;
        return objectElement.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private void LogUnavailable(string reason) =>
        _logger.LogWarning(EventTemplate, Operation, Module, "unavailable", reason);
}
