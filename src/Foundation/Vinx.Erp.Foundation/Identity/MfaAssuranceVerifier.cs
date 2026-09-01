using System.Collections.Concurrent;

namespace Vinx.Erp.Foundation.Identity;

public enum MfaAssuranceVerificationState
{
    VerifiedFactorPresent,
    VerifiedFactorAbsent,
    Unavailable,
}

public sealed record MfaAssuranceVerificationResult(MfaAssuranceVerificationState State)
{
    public static MfaAssuranceVerificationResult VerifiedFactorPresent() =>
        new(MfaAssuranceVerificationState.VerifiedFactorPresent);

    public static MfaAssuranceVerificationResult VerifiedFactorAbsent() =>
        new(MfaAssuranceVerificationState.VerifiedFactorAbsent);

    public static MfaAssuranceVerificationResult Unavailable() =>
        new(MfaAssuranceVerificationState.Unavailable);
}

/// <summary>
/// Contrato da re-derivação de AAL2 no uso do privilégio: pergunta ao provedor de
/// identidade se o sujeito ainda possui pelo menos um fator MFA verificado agora.
/// Qualquer falha de verificação é reportada como indisponível e tratada como
/// negação pelo chamador (fail closed).
/// </summary>
public interface IMfaAssuranceVerifier
{
    Task<MfaAssuranceVerificationResult> VerifyAsync(
        string subject,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Cache de resultado de verificação de MFA por sujeito com expiração fixa.
/// Somente resultados definitivos (fator presente/ausente) devem ser cacheados;
/// indisponibilidade não é cacheada para que a próxima requisição tente novamente.
/// O volume é limitado: apenas operadores de plataforma com cookie aal2 chegam aqui.
/// </summary>
public sealed class MfaAssuranceCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public MfaAssuranceCache()
        : this(TimeProvider.System)
    {
    }

    internal MfaAssuranceCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public bool TryGet(string subject, out MfaAssuranceVerificationState state)
    {
        state = MfaAssuranceVerificationState.Unavailable;
        if (
            !string.IsNullOrWhiteSpace(subject)
            && _entries.TryGetValue(subject, out var entry)
            && entry.ExpiresAt > _timeProvider.GetUtcNow()
        )
        {
            state = entry.State;
            return true;
        }

        _entries.TryRemove(subject, out _);
        return false;
    }

    public void Set(string subject, MfaAssuranceVerificationState state)
    {
        if (string.IsNullOrWhiteSpace(subject)
            || state is MfaAssuranceVerificationState.Unavailable)
        {
            return;
        }

        _entries[subject] = new CacheEntry(
            state,
            _timeProvider.GetUtcNow().Add(Lifetime));
    }

    private readonly record struct CacheEntry(
        MfaAssuranceVerificationState State,
        DateTimeOffset ExpiresAt);
}
