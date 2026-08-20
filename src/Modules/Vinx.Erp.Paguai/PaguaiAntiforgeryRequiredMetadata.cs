using Vinx.Erp.Foundation.Identity;

namespace Vinx.Erp.Paguai;

/// <summary>Marcador de antiforgery exigido na operação (mantém o contrato OpenAPI e o middleware).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PaguaiAntiforgeryRequiredMetadata : Attribute, IAntiforgeryRequiredMetadata
{
    /// <summary>Bearer dispensa o token; somente a sessão de cookie BFF o exige.</summary>
    public bool CookieAuthenticatedOnly => true;
}
