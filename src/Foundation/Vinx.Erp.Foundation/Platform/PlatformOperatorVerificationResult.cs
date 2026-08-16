using System.Diagnostics.CodeAnalysis;

namespace Vinx.Erp.Foundation.Platform;

public enum PlatformOperatorVerificationCategory
{
    Verified,
    NotFound,
    SubjectMismatch,
    InvalidUser,
    ProviderUnavailable,
}

public sealed class PlatformOperatorVerificationResult
{
    private readonly string? _verifiedEmail;

    private PlatformOperatorVerificationResult(
        PlatformOperatorVerificationCategory category,
        string? verifiedEmail = null
    )
    {
        Category = category;
        _verifiedEmail = verifiedEmail;
    }

    public PlatformOperatorVerificationCategory Category { get; }

    public bool IsVerified => Category is PlatformOperatorVerificationCategory.Verified;

    /// <summary>
    /// Disponibiliza o e-mail validado somente para a criação interna do operador.
    /// </summary>
    public bool TryGetVerifiedEmail([NotNullWhen(true)] out string? email)
    {
        email = _verifiedEmail;
        return IsVerified && !string.IsNullOrWhiteSpace(email);
    }

    public override string ToString() => Category.ToString();

    public static PlatformOperatorVerificationResult Verified(string email) =>
        new(PlatformOperatorVerificationCategory.Verified, email);

    public static PlatformOperatorVerificationResult Rejected(
        PlatformOperatorVerificationCategory category
    ) => new(category);
}
