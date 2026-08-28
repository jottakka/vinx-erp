using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.Foundation.Platform;

public sealed class SupabaseAdminOptions
{
    public const string ServiceRoleKeyConfigurationKey = "SUPABASE_AUTH_SERVICE_ROLE_KEY";
    public const string InviteRedirectUrlConfigurationKey = "SUPABASE_INVITE_REDIRECT_URL";

    [ConfigurationKeyName(ServiceRoleKeyConfigurationKey)]
    public string ServiceRoleKey { get; set; } = string.Empty;

    [ConfigurationKeyName(InviteRedirectUrlConfigurationKey)]
    public string InviteRedirectUrl { get; set; } = string.Empty;
}
