using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace Vinx.Erp.Foundation.Identity;

public sealed class SupabaseIdentityProvider(string issuer) : IIdentityProvider
{
    private const string JsonClaimValueType = "JSON";
    private const string JsonArrayClaimValueType = "JSON_ARRAY";

    private readonly string _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    public bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof proof)
    {
        proof = null!;

        if (!TryGetSingleScalarClaim(principal, "iss", out var issuer)
            || !TryGetSingleScalarClaim(principal, "sub", out var subject)
            || !TryGetSingleScalarClaim(principal, "session_id", out var sessionIdValue)
            || !TryGetSingleScalarClaim(principal, "iat", out var issuedAtValue)
            || !TryGetSingleScalarClaim(principal, "exp", out var expiresAtValue)
            || string.IsNullOrWhiteSpace(issuer)
            || !string.Equals(issuer, _issuer, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(subject)
            || !Guid.TryParseExact(sessionIdValue, "D", out var sessionId)
            || sessionId == Guid.Empty
            || !TryGetUnixTime(issuedAtValue, out var issuedAt)
            || !TryGetUnixTime(expiresAtValue, out var expiresAt)
            || expiresAt <= issuedAt
            || !TryGetProviderAuthenticatedAt(principal, issuedAt, out var authenticatedAt)
            || !TryGetAssuranceLevel(principal, out var assuranceLevel))
        {
            return false;
        }

        proof = new ExternalIdentityProof(
            new ExternalIdentityKey(issuer, subject),
            sessionId,
            authenticatedAt,
            expiresAt,
            assuranceLevel);
        return true;
    }

    private static bool TryGetSingleScalarClaim(ClaimsPrincipal principal, string type, out string value)
    {
        value = null!;
        var claims = principal.Claims
            .Where(claim => string.Equals(claim.Type, type, StringComparison.Ordinal))
            .ToArray();

        if (claims.Length != 1 || !IsScalar(claims[0]))
        {
            return false;
        }

        value = claims[0].Value;
        return true;
    }

    private static bool IsScalar(Claim claim) =>
        !string.Equals(claim.ValueType, JsonClaimValueType, StringComparison.Ordinal)
        && !string.Equals(claim.ValueType, JsonArrayClaimValueType, StringComparison.Ordinal);

    private static bool TryGetAssuranceLevel(
        ClaimsPrincipal principal,
        out AuthenticatorAssuranceLevel assuranceLevel)
    {
        assuranceLevel = AuthenticatorAssuranceLevel.Aal1;
        var claims = principal.Claims
            .Where(claim => string.Equals(claim.Type, "aal", StringComparison.Ordinal))
            .ToArray();

        if (claims.Length == 0)
        {
            return true;
        }

        if (claims.Length != 1 || !IsScalar(claims[0]))
        {
            return false;
        }

        assuranceLevel = claims[0].Value switch
        {
            "aal1" => AuthenticatorAssuranceLevel.Aal1,
            "aal2" => AuthenticatorAssuranceLevel.Aal2,
            _ => default,
        };
        return claims[0].Value is "aal1" or "aal2";
    }

    private static bool TryGetUnixTime(string value, out DateTimeOffset unixTime)
    {
        unixTime = default;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        try
        {
            unixTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetProviderAuthenticatedAt(
        ClaimsPrincipal principal,
        DateTimeOffset issuedAt,
        out DateTimeOffset authenticatedAt)
    {
        authenticatedAt = default;
        var amrClaims = principal.Claims
            .Where(claim => string.Equals(claim.Type, "amr", StringComparison.Ordinal))
            .ToArray();

        if (amrClaims.Length == 0
            || amrClaims.Any(claim => !string.Equals(
                claim.ValueType,
                JsonClaimValueType,
                StringComparison.Ordinal)))
        {
            return false;
        }

        DateTimeOffset? earliestAuthentication = null;
        foreach (var amrClaim in amrClaims)
        {
            if (!TryGetAmrEntry(amrClaim.Value, out var method, out var timestamp))
            {
                return false;
            }

            if (!TryGetUnixTime(timestamp, out var authenticationTime))
            {
                return false;
            }

            if (string.Equals(method, "token_refresh", StringComparison.Ordinal))
            {
                continue;
            }

            if (authenticationTime > issuedAt)
            {
                return false;
            }

            if (earliestAuthentication is null || authenticationTime < earliestAuthentication)
            {
                earliestAuthentication = authenticationTime;
            }
        }

        if (earliestAuthentication is null)
        {
            return false;
        }

        authenticatedAt = earliestAuthentication.Value;
        return true;
    }

    private static bool TryGetAmrEntry(string value, out string method, out string timestamp)
    {
        method = null!;
        timestamp = null!;

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var methodCount = 0;
            var timestampCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "method", StringComparison.Ordinal))
                {
                    methodCount++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    method = property.Value.GetString()!;
                }
                else if (string.Equals(property.Name, "timestamp", StringComparison.Ordinal))
                {
                    timestampCount++;
                    if (property.Value.ValueKind != JsonValueKind.Number)
                    {
                        return false;
                    }

                    timestamp = property.Value.GetRawText();
                }
            }

            return methodCount == 1
                && !string.IsNullOrWhiteSpace(method)
                && timestampCount == 1
                && long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
