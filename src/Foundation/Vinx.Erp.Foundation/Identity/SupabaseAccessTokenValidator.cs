using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Vinx.Erp.Foundation.Identity;

public enum SupabaseAccessTokenValidationState
{
    Valid,
    Invalid,
    Unavailable,
}

/// <summary>
/// Resultado da validação local de um access token do Supabase sem manter o token após a composição da sessão.
/// </summary>
public sealed record SupabaseAccessTokenValidationResult(
    SupabaseAccessTokenValidationState State,
    ClaimsPrincipal? Principal = null)
{
    public static SupabaseAccessTokenValidationResult Valid(ClaimsPrincipal principal) =>
        new(SupabaseAccessTokenValidationState.Valid, principal);

    public static SupabaseAccessTokenValidationResult Invalid() =>
        new(SupabaseAccessTokenValidationState.Invalid);

    public static SupabaseAccessTokenValidationResult Unavailable() =>
        new(SupabaseAccessTokenValidationState.Unavailable);
}

/// <summary>
/// Valida o JWT retornado pelo password grant com a mesma configuração de issuer, audiência e chaves do bearer.
/// </summary>
public sealed class SupabaseAccessTokenValidator(JwtBearerOptions options)
{
    public async Task<SupabaseAccessTokenValidationResult> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192)
        {
            return SupabaseAccessTokenValidationResult.Invalid();
        }

        try
        {
            var validationParameters = options.TokenValidationParameters.Clone();
            if (options.ConfigurationManager is not null)
            {
                var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
                validationParameters.IssuerSigningKeys = configuration.SigningKeys;
            }

            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = options.MapInboundClaims,
            };
            var principal = handler.ValidateToken(accessToken, validationParameters, out _);
            return SupabaseAccessTokenValidationResult.Valid(principal);
        }
        catch (OperationCanceledException exception)
            when (exception.CancellationToken.CanBeCanceled
                && exception.CancellationToken == cancellationToken)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SupabaseAccessTokenValidationResult.Unavailable();
        }
        catch (SecurityTokenException)
        {
            return SupabaseAccessTokenValidationResult.Invalid();
        }
        catch (ArgumentException)
        {
            return SupabaseAccessTokenValidationResult.Invalid();
        }
        catch (HttpRequestException)
        {
            return SupabaseAccessTokenValidationResult.Unavailable();
        }
        catch (InvalidOperationException)
        {
            return SupabaseAccessTokenValidationResult.Unavailable();
        }
    }
}
