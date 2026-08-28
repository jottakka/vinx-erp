using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.Foundation.Files;

public sealed class SupabaseStorageOptions
{
    public const string BaseUriConfigurationKey = "SUPABASE_STORAGE_URL";
    public const string ServiceRoleKeyConfigurationKey = "SUPABASE_STORAGE_SERVICE_ROLE_KEY";
    public const string PrivateContainerId = "vinx-foundation-private";

    [ConfigurationKeyName(BaseUriConfigurationKey)]
    public Uri? BaseUri { get; set; }

    [ConfigurationKeyName(ServiceRoleKeyConfigurationKey)]
    public string ServiceRoleKey { get; set; } = string.Empty;
}
