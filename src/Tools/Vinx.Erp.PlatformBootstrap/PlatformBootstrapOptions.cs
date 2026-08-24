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

public sealed class ExternalIdentityReprovisioningOptions
{
    [ConfigurationKeyName("REPROVISION_IDENTITY_USER_ID")]
    public string UserId { get; set; } = string.Empty;

    [ConfigurationKeyName("REPROVISION_IDENTITY_EXPECTED_EXTERNAL_IDENTITY_ID")]
    public string ExpectedExternalIdentityId { get; set; } = string.Empty;

    [ConfigurationKeyName("REPROVISION_IDENTITY_EXPECTED_OLD_SUBJECT")]
    public string ExpectedOldSubject { get; set; } = string.Empty;

    [ConfigurationKeyName("REPROVISION_IDENTITY_NEW_SUBJECT")]
    public string NewSubject { get; set; } = string.Empty;
}
