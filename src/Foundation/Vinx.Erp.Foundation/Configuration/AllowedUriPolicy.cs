using Microsoft.Extensions.Hosting;

namespace Vinx.Erp.Foundation.Configuration;

/// <summary>
/// Política canônica de URLs de configuração permitidas (issuers de
/// identidade, endpoints de storage/rede e URLs de produto): HTTPS
/// obrigatório, sem UserInfo nem Fragment, com a única exceção de HTTP
/// loopback em Development. Substitui os validadores quase idênticos que
/// divergiam entre o WorkerHost, o PlatformBootstrap, o adapter Meta e a
/// autenticação Supabase.
/// </summary>
public static class AllowedUriPolicy
{
    /// <summary>
    /// HTTPS sempre; HTTP somente para loopback em Development. Rejeita
    /// URLs com UserInfo (credenciais embutidas) ou Fragment.
    /// </summary>
    public static bool IsAllowedHttp(string value, IHostEnvironment environment) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (
            uri.Scheme == Uri.UriSchemeHttps
            || (
                environment.IsDevelopment()
                && uri.Scheme == Uri.UriSchemeHttp
                && uri.IsLoopback
            )
        );

    /// <summary>
    /// HTTPS obrigatório sem exceção de desenvolvimento (URLs visíveis a
    /// terceiros, como base do webapp Meta). Rejeita UserInfo e Fragment.
    /// </summary>
    public static bool IsAllowedHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.Scheme == Uri.UriSchemeHttps;
}
