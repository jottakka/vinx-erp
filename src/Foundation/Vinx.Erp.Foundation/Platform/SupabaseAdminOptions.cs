using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.Foundation.Platform;

public sealed class SupabaseAdminOptions
{
    public const string ServiceRoleKeyConfigurationKey = "SUPABASE_AUTH_SERVICE_ROLE_KEY";

    [ConfigurationKeyName(ServiceRoleKeyConfigurationKey)]
    public string ServiceRoleKey { get; set; } = string.Empty;
}
