using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.Foundation.Identity;

/// <summary>
/// Chave pública usada somente pelo BFF do servidor para solicitar uma sessão ao Supabase Auth.
/// </summary>
public sealed class SupabaseBrowserSessionOptions
{
    public const string PublishableKeyConfigurationKey = "SUPABASE_AUTH_PUBLISHABLE_KEY";

    [ConfigurationKeyName(PublishableKeyConfigurationKey)]
    public string PublishableKey { get; set; } = string.Empty;
}
