namespace Vinx.Erp.Foundation.Identity;

/// <summary>
/// Contrato de antiforgery exigido na operação: diz se o token é dispensado para
/// requisições autenticadas por Bearer (somente a sessão de cookie BFF o exige).
/// </summary>
public interface IAntiforgeryRequiredMetadata
{
    /// <summary>Quando true, apenas a sessão BFF (cookie) exige o token; Bearer dispensa.</summary>
    bool CookieAuthenticatedOnly { get; }
}
