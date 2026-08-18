using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Platform;

/// <summary>Envia o convite inicial pelo Admin API sem reter token, e-mail ou corpo do provedor.</summary>
public sealed class SupabaseAdminOwnerInvitationClient
{
    public const string HttpClientName = "supabase-owner-invitation";
    private const int MaximumResponseBytes = 32 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _serviceRoleKey;
    private readonly string _inviteRedirectUrl;

    public SupabaseAdminOwnerInvitationClient(
        HttpClient httpClient,
        SupabaseAuthOptions authentication,
        SupabaseAdminOptions administration
    )
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(administration);

        _httpClient = httpClient;
        _issuer = authentication.NormalizedIssuer;
        _audience = authentication.Audience;
        _serviceRoleKey = administration.ServiceRoleKey;
        _inviteRedirectUrl = administration.InviteRedirectUrl;

        if (
            string.IsNullOrWhiteSpace(_audience)
            || string.IsNullOrWhiteSpace(_serviceRoleKey)
            || string.IsNullOrWhiteSpace(_inviteRedirectUrl)
        )
        {
            throw new ArgumentException("Supabase owner invitation options must be configured.");
        }
    }

    public async Task<SupabaseOwnerInvitationResult> InviteAsync(
        string ownerEmail,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            return SupabaseOwnerInvitationResult.Rejected();
        }

        var endpoint = new Uri($"{_issuer.TrimEnd('/')}/invite", UriKind.Absolute);
        var requestUri = new UriBuilder(endpoint)
        {
            Query = $"redirect_to={Uri.EscapeDataString(_inviteRedirectUrl)}",
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new SupabaseOwnerInvitationRequest(ownerEmail)),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            if (!response.IsSuccessStatusCode)
            {
                return
                    response.StatusCode
                        is HttpStatusCode.BadRequest
                            or HttpStatusCode.Conflict
                            or HttpStatusCode.UnprocessableEntity
                    ? SupabaseOwnerInvitationResult.Rejected()
                    : SupabaseOwnerInvitationResult.Ambiguous();
            }

            var payload = await ReadResponseAsync(response, cancellationToken);
            if (payload is null)
            {
                return SupabaseOwnerInvitationResult.Ambiguous();
            }

            using var document = JsonDocument.Parse(payload);
            return TryGetInvitedSubject(document.RootElement, ownerEmail, out var subject)
                ? SupabaseOwnerInvitationResult.Succeeded(subject)
                : SupabaseOwnerInvitationResult.Ambiguous();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SupabaseOwnerInvitationResult.Ambiguous();
        }
        catch (HttpRequestException)
        {
            return SupabaseOwnerInvitationResult.Ambiguous();
        }
        catch (IOException)
        {
            return SupabaseOwnerInvitationResult.Ambiguous();
        }
        catch (JsonException)
        {
            return SupabaseOwnerInvitationResult.Ambiguous();
        }
    }

    private bool TryGetInvitedSubject(JsonElement user, string ownerEmail, out Guid subject)
    {
        subject = Guid.Empty;
        if (
            user.ValueKind is not JsonValueKind.Object
            || !TryGetRequiredString(user, "id", out var subjectText)
            || !Guid.TryParse(subjectText, out subject)
            || subject == Guid.Empty
            || !TryGetRequiredString(user, "aud", out var audience)
            || !string.Equals(audience, _audience, StringComparison.Ordinal)
            || !TryGetRequiredString(user, "email", out var returnedEmail)
            || !string.Equals(returnedEmail, ownerEmail, StringComparison.OrdinalIgnoreCase)
            || !HasFalseBoolean(user, "is_anonymous")
            || !IsMissingOrNull(user, "banned_until")
            || !IsMissingOrNull(user, "deleted_at")
        )
        {
            subject = Guid.Empty;
            return false;
        }

        return true;
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

    private static bool TryGetRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool HasFalseBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.False;

    private static bool IsMissingOrNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null;

    private sealed record SupabaseOwnerInvitationRequest(
        [property: JsonPropertyName("email")] string Email
    );
}

public enum SupabaseOwnerInvitationResultKind
{
    Succeeded,
    Rejected,
    Ambiguous,
}

public sealed record SupabaseOwnerInvitationResult(
    SupabaseOwnerInvitationResultKind Kind,
    Guid? Subject
)
{
    public static SupabaseOwnerInvitationResult Succeeded(Guid subject) =>
        new(SupabaseOwnerInvitationResultKind.Succeeded, subject);

    public static SupabaseOwnerInvitationResult Rejected() =>
        new(SupabaseOwnerInvitationResultKind.Rejected, null);

    public static SupabaseOwnerInvitationResult Ambiguous() =>
        new(SupabaseOwnerInvitationResultKind.Ambiguous, null);
}
