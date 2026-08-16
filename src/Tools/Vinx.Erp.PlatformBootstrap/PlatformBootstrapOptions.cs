using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.PlatformBootstrap;

public sealed class PlatformBootstrapDatabaseOptions
{
    [ConfigurationKeyName("FOUNDATION_MIGRATOR_CONNECTION")]
    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class PlatformOperatorDeploymentOptions
{
    [ConfigurationKeyName("PLATFORM_OPERATOR_SUBJECT")]
    public string Subject { get; set; } = string.Empty;

    [ConfigurationKeyName("PLATFORM_OPERATOR_DISPLAY_NAME")]
    public string DisplayName { get; set; } = string.Empty;
}
