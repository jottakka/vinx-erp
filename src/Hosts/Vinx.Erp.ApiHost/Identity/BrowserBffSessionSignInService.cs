using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.ApiHost.Identity;

internal enum BrowserBffSessionSignInState
{
    Succeeded,
    Unauthorized,
    RateLimited,
    Unavailable,
}

internal sealed record BrowserBffSessionSignInResult(
    BrowserBffSessionSignInState State,
    ClaimsPrincipal? Principal = null,
    DateTimeOffset? ExpiresUtc = null)
{
    public static BrowserBffSessionSignInResult Succeeded(
        ClaimsPrincipal principal,
        DateTimeOffset expiresUtc) => new(BrowserBffSessionSignInState.Succeeded, principal, expiresUtc);

    public static BrowserBffSessionSignInResult Unauthorized() =>
        new(BrowserBffSessionSignInState.Unauthorized);

    public static BrowserBffSessionSignInResult RateLimited() =>
        new(BrowserBffSessionSignInState.RateLimited);

    public static BrowserBffSessionSignInResult Unavailable() =>
        new(BrowserBffSessionSignInState.Unavailable);
}

/// <summary>
/// Converte uma prova de password grant já validada em sessão de browser sem reter tokens do provider.
/// </summary>
internal sealed class BrowserBffSessionSignInService(
    SupabasePasswordSessionClient passwordSessionClient,
    SupabaseAccessTokenValidator accessTokenValidator,
    IIdentityProvider identityProvider,
    InternalSessionAuthenticator internalSessionAuthenticator,
    TimeProvider timeProvider,
    ILogger<BrowserBffSessionSignInService> logger)
{
    private const string EventTemplate = "Browser session {Operation} {Module} {Result}";

    public async Task<BrowserBffSessionSignInResult> SignInAsync(
        BrowserBffSessionSignInCommand command,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var passwordSession = await passwordSessionClient.SignInAsync(
            command.Email,
            command.Password,
            cancellationToken);
        if (passwordSession.State == SupabasePasswordSessionState.InvalidCredentials)
        {
            return BrowserBffSessionSignInResult.Unauthorized();
        }

        if (passwordSession.State == SupabasePasswordSessionState.RateLimited)
        {
            logger.LogWarning(EventTemplate, "sign_in", "identity", "rate_limited");
            return BrowserBffSessionSignInResult.RateLimited();
        }

        if (passwordSession.State != SupabasePasswordSessionState.Succeeded
            || !passwordSession.TryGetAccessToken(out var accessToken))
        {
            logger.LogWarning(EventTemplate, "sign_in", "identity", "provider_unavailable");
            return BrowserBffSessionSignInResult.Unavailable();
        }

        var validation = await accessTokenValidator.ValidateAsync(accessToken, cancellationToken);
        if (validation.State != SupabaseAccessTokenValidationState.Valid
            || validation.Principal is null
            || !identityProvider.TryGetExternalIdentityProof(validation.Principal, out var proof)
            || proof.TokenExpiresAt <= timeProvider.GetUtcNow())
        {
            logger.LogWarning(EventTemplate, "sign_in", "identity", "provider_proof_invalid");
            return BrowserBffSessionSignInResult.Unavailable();
        }

        var admission = await internalSessionAuthenticator.AuthenticateAsync(
            validation.Principal,
            correlationId,
            cancellationToken);
        if (admission.State == InternalSessionAuthenticationState.Unauthenticated)
        {
            return BrowserBffSessionSignInResult.Unauthorized();
        }

        if (admission.State != InternalSessionAuthenticationState.Authenticated
            || !BrowserBffSessionProofPrincipalFactory.TryCreate(
                validation.Principal,
                proof,
                out var principal))
        {
            logger.LogWarning(EventTemplate, "sign_in", "identity", "identity_gate_unavailable");
            return BrowserBffSessionSignInResult.Unavailable();
        }

        return BrowserBffSessionSignInResult.Succeeded(principal, proof.TokenExpiresAt);
    }
}

internal static class BrowserBffSessionProofPrincipalFactory
{
    private static readonly string[] RequiredScalarClaimTypes =
    ["iss", "sub", "session_id", "iat", "exp"];

    public static bool TryCreate(
        ClaimsPrincipal providerPrincipal,
        ExternalIdentityProof proof,
        out ClaimsPrincipal principal)
    {
        principal = null!;
        var claims = new List<Claim>();
        foreach (var type in RequiredScalarClaimTypes)
        {
            var values = providerPrincipal.FindAll(type).ToArray();
            if (values.Length != 1 || IsJson(values[0]))
            {
                return false;
            }

            claims.Add(Copy(values[0]));
        }

        if (!string.Equals(claims[0].Value, proof.Key.Issuer, StringComparison.Ordinal)
            || !string.Equals(claims[1].Value, proof.Key.Subject, StringComparison.Ordinal)
            || !Guid.TryParseExact(claims[2].Value, "D", out var sessionId)
            || sessionId != proof.ProviderSessionId)
        {
            return false;
        }

        var amrClaims = providerPrincipal.FindAll("amr").ToArray();
        if (amrClaims.Length == 0 || !TryAddCanonicalAmrClaims(amrClaims, claims))
        {
            return false;
        }

        var assuranceClaims = providerPrincipal.FindAll("aal").ToArray();
        if (assuranceClaims.Length > 1
            || assuranceClaims.Any(IsJson)
            || (assuranceClaims.Length == 0
                && proof.AssuranceLevel != AuthenticatorAssuranceLevel.Aal1)
            || (assuranceClaims.Length == 1
                && assuranceClaims[0].Value != ToClaimValue(proof.AssuranceLevel)))
        {
            return false;
        }

        claims.AddRange(assuranceClaims.Select(Copy));
        principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            BrowserBffAuthenticationExtensions.CookieScheme));
        return true;
    }

    private static bool IsJson(Claim claim) =>
        string.Equals(claim.ValueType, "JSON", StringComparison.Ordinal)
        || string.Equals(claim.ValueType, "JSON_ARRAY", StringComparison.Ordinal);

    private static bool TryAddCanonicalAmrClaims(IEnumerable<Claim> amrClaims, List<Claim> claims)
    {
        foreach (var claim in amrClaims)
        {
            if (!IsJson(claim) || !TryCanonicalizeAmrClaim(claim.Value, out var canonicalValue))
            {
                return false;
            }

            claims.Add(new Claim("amr", canonicalValue, "JSON"));
        }

        return true;
    }

    private static bool TryCanonicalizeAmrClaim(string value, out string canonicalValue)
    {
        canonicalValue = null!;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            string? method = null;
            long? timestamp = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("method"))
                {
                    if (method is not null || property.Value.ValueKind is not JsonValueKind.String)
                    {
                        return false;
                    }

                    method = property.Value.GetString();
                }
                else if (property.NameEquals("timestamp"))
                {
                    if (timestamp is not null
                        || property.Value.ValueKind is not JsonValueKind.Number
                        || !property.Value.TryGetInt64(out var parsedTimestamp))
                    {
                        return false;
                    }

                    timestamp = parsedTimestamp;
                }
            }

            if (string.IsNullOrWhiteSpace(method) || timestamp is null)
            {
                return false;
            }

            canonicalValue = JsonSerializer.Serialize(new { method, timestamp });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Claim Copy(Claim claim) =>
        new(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer);

    private static string ToClaimValue(AuthenticatorAssuranceLevel assuranceLevel) => assuranceLevel switch
    {
        AuthenticatorAssuranceLevel.Aal1 => "aal1",
        AuthenticatorAssuranceLevel.Aal2 => "aal2",
        _ => throw new ArgumentOutOfRangeException(nameof(assuranceLevel)),
    };
}
