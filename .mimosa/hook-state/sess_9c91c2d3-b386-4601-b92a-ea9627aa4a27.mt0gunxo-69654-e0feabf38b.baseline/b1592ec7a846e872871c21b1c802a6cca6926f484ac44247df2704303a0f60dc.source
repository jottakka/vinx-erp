using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Vinx.Erp.Foundation.Identity;

public enum SupabasePasswordSessionState
{
    Succeeded,
    InvalidCredentials,
    RateLimited,
    Unavailable,
}

/// <summary>
/// Resultado de um password grant que preserva o access token somente durante a composição da sessão BFF.
/// </summary>
public sealed class SupabasePasswordSessionResult
{
    private readonly string? accessToken;

    private SupabasePasswordSessionResult(SupabasePasswordSessionState state, string? accessToken = null)
    {
        State = state;
        this.accessToken = accessToken;
    }

    public SupabasePasswordSessionState State { get; }

    public static SupabasePasswordSessionResult Succeeded(string accessToken) =>
        new(SupabasePasswordSessionState.Succeeded, accessToken);

    public static SupabasePasswordSessionResult InvalidCredentials() =>
        new(SupabasePasswordSessionState.InvalidCredentials);

    public static SupabasePasswordSessionResult RateLimited() =>
        new(SupabasePasswordSessionState.RateLimited);

    public static SupabasePasswordSessionResult Unavailable() =>
        new(SupabasePasswordSessionState.Unavailable);

    public bool TryGetAccessToken([NotNullWhen(true)] out string? value)
    {
        value = accessToken;
        return State == SupabasePasswordSessionState.Succeeded && !string.IsNullOrWhiteSpace(value);
    }

    public override string ToString() => $"SupabasePasswordSessionResult {{ State = {State} }}";
}

/// <summary>
/// Executa o único password grant permitido ao BFF sem reter refresh token ou detalhes brutos do provider.
/// </summary>
public sealed class SupabasePasswordSessionClient(
    HttpClient httpClient,
    SupabaseBrowserSessionOptions options)
{
    private const int MaximumProviderResponseBytes = 64 * 1024;

    public async Task<SupabasePasswordSessionResult> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "token?grant_type=password")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("apikey", options.PublishableKey);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return SupabasePasswordSessionResult.InvalidCredentials();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return SupabasePasswordSessionResult.RateLimited();
            }

            if (!response.IsSuccessStatusCode)
            {
                return SupabasePasswordSessionResult.Unavailable();
            }

            if (response.Content.Headers.ContentLength is > MaximumProviderResponseBytes)
            {
                return SupabasePasswordSessionResult.Unavailable();
            }

            var payload = await ReadBoundedAsync(response.Content, cancellationToken);
            return TryGetAccessToken(payload, out var accessToken)
                ? SupabasePasswordSessionResult.Succeeded(accessToken)
                : SupabasePasswordSessionResult.Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SupabasePasswordSessionResult.Unavailable();
        }
        catch (HttpRequestException)
        {
            return SupabasePasswordSessionResult.Unavailable();
        }
        catch (IOException)
        {
            return SupabasePasswordSessionResult.Unavailable();
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumProviderResponseBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool TryGetAccessToken(byte[]? payload, out string accessToken)
    {
        accessToken = null!;
        if (payload is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            string? value = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "access_token", StringComparison.Ordinal))
                {
                    continue;
                }

                if (value is not null || property.Value.ValueKind is not JsonValueKind.String)
                {
                    return false;
                }

                value = property.Value.GetString();
            }

            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192)
            {
                return false;
            }

            accessToken = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
