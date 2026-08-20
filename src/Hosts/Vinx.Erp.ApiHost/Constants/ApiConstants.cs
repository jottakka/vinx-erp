namespace Vinx.Erp.ApiHost.Constants;

/// <summary>Constantes de API compartilhadas entre middlewares, controllers e contratos.</summary>
internal static class ApiConstants
{
    /// <summary>Limite de corpo JSON por bytes efetivamente lidos, inclusive transferência chunked.</summary>
    public const int MaximumJsonBodyBytes = 24 * 1024;

    /// <summary>Policy de rate limit das rotas protegidas da API.</summary>
    public const string ProtectedRateLimitPolicy = "foundation";

    /// <summary>
    /// Maior valor monetário inteiro (em unidades menores da moeda) que o JSON
    /// transporta sem perda de precisão em clientes IEEE 754
    /// (<c>Number.MAX_SAFE_INTEGER</c>). Mantém <c>amountMinor</c> seguro no
    /// transporte numérico do contrato.
    /// </summary>
    public const long MaximumAmountMinor = 9_007_199_254_740_991;
}
