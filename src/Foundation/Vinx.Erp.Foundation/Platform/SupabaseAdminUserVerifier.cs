using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Foundation.Platform;

/// <summary>
/// Confirma um sujeito de deployment no endpoint administrativo do Supabase sem expor dados do provedor.
/// </summary>
public sealed class SupabaseAdminUserVerifier
{
    private readonly HttpClient _httpClient;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _serviceRoleKey;

    public SupabaseAdminUserVerifier(
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

        if (string.IsNullOrWhiteSpace(_audience))
        {
            throw new ArgumentException(
                "Supabase Auth audience must be configured.",
                nameof(authentication)
            );
        }

        if (string.IsNullOrWhiteSpace(_serviceRoleKey))
        {
            throw new ArgumentException(
                "Supabase Admin service role key must be configured.",
                nameof(administration)
            );
        }
    }

    public async Task<PlatformOperatorVerificationResult> VerifyAsync(
        string subject,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return InvalidUser();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_issuer}/admin/users/{Uri.EscapeDataString(subject)}"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                return Rejected(PlatformOperatorVerificationCategory.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ProviderUnavailable();
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken
            );
            if (
                document.RootElement.ValueKind is JsonValueKind.Object
                && HasRequiredString(document.RootElement, "id", out var returnedSubject)
                && !string.Equals(returnedSubject, subject, StringComparison.Ordinal)
            )
            {
                return Rejected(PlatformOperatorVerificationCategory.SubjectMismatch);
            }

            return TryGetVerifiedEmail(document.RootElement, subject, out var email)
                ? PlatformOperatorVerificationResult.Verified(email)
                : InvalidUser();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            return ProviderUnavailable();
        }
        catch (JsonException)
        {
            return InvalidUser();
        }
    }

    private bool TryGetVerifiedEmail(
        JsonElement user,
        string subject,
        [NotNullWhen(true)] out string? email
    )
    {
        email = null;
        if (user.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (!HasRequiredString(user, "id", out var returnedSubject))
        {
            return false;
        }

        if (!string.Equals(returnedSubject, subject, StringComparison.Ordinal))
        {
            return false;
        }

        if (!HasRequiredString(user, "email", out var providerEmail))
        {
            return false;
        }

        var isVerified =
            HasRequiredString(user, "aud", out var audience)
            && string.Equals(audience, _audience, StringComparison.Ordinal)
            && HasFalseBoolean(user, "is_anonymous")
            && HasRequiredString(user, "email_confirmed_at", out _)
            && HasMatchingProviderIdentity(user, subject)
            && IsMissingOrNull(user, "banned_until")
            && IsMissingOrNull(user, "deleted_at");
        if (!isVerified)
        {
            return false;
        }

        email = providerEmail;
        return true;
    }

    private static bool HasRequiredString(JsonElement objectElement, string name, out string value)
    {
        value = string.Empty;
        return objectElement.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool HasFalseBoolean(JsonElement objectElement, string name) =>
        objectElement.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.False;

    private static bool IsMissingOrNull(JsonElement objectElement, string name) =>
        !objectElement.TryGetProperty(name, out var property)
        || property.ValueKind is JsonValueKind.Null;

    private static bool HasMatchingProviderIdentity(JsonElement user, string subject)
    {
        if (
            !user.TryGetProperty("identities", out var identities)
            || identities.ValueKind is not JsonValueKind.Array
        )
        {
            return false;
        }

        foreach (var identity in identities.EnumerateArray())
        {
            if (
                identity.ValueKind is JsonValueKind.Object
                && HasRequiredString(identity, "user_id", out var userId)
                && string.Equals(userId, subject, StringComparison.Ordinal)
                && HasRequiredString(identity, "identity_id", out _)
                && HasRequiredString(identity, "provider", out _)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static PlatformOperatorVerificationResult InvalidUser() =>
        Rejected(PlatformOperatorVerificationCategory.InvalidUser);

    private static PlatformOperatorVerificationResult ProviderUnavailable() =>
        Rejected(PlatformOperatorVerificationCategory.ProviderUnavailable);

    private static PlatformOperatorVerificationResult Rejected(
        PlatformOperatorVerificationCategory category
    ) => PlatformOperatorVerificationResult.Rejected(category);
}
