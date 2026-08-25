namespace Vinx.Erp.ApiHost.Constants;

/// <summary>Constantes de API compartilhadas entre middlewares, controllers e contratos.</summary>
internal static class ApiConstants
{
    /// <summary>Limite de corpo JSON por bytes efetivamente lidos, inclusive transferência chunked.</summary>
    public const int MaximumJsonBodyBytes = 24 * 1024;

    /// <summary>Teto de anexos distintos por execução do agente.</summary>
    public const int MaxAgentAttachments = 3;

    /// <summary>Policy de rate limit das rotas protegidas da API.</summary>
    public const string ProtectedRateLimitPolicy = "foundation";

}
