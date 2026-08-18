using Microsoft.Extensions.Configuration;

namespace Vinx.Erp.ApiHost.Identity;

internal sealed class BrowserBffSessionProtectionOptions
{
    public const string KeyDirectoryConfigurationKey = "BROWSER_SESSION_DATA_PROTECTION_KEY_DIRECTORY";
    public const string ApplicationNameConfigurationKey = "BROWSER_SESSION_DATA_PROTECTION_APPLICATION_NAME";
    public const string CertificatePathConfigurationKey = "BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PATH";
    public const string CertificatePasswordConfigurationKey = "BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PASSWORD";

    [ConfigurationKeyName(KeyDirectoryConfigurationKey)]
    public string KeyDirectory { get; set; } = string.Empty;

    [ConfigurationKeyName(ApplicationNameConfigurationKey)]
    public string ApplicationName { get; set; } = string.Empty;

    [ConfigurationKeyName(CertificatePathConfigurationKey)]
    public string CertificatePath { get; set; } = string.Empty;

    [ConfigurationKeyName(CertificatePasswordConfigurationKey)]
    public string CertificatePassword { get; set; } = string.Empty;

    public bool HasProductionKeyRingConfiguration() =>
        !string.IsNullOrWhiteSpace(KeyDirectory)
        && !string.IsNullOrWhiteSpace(ApplicationName)
        && !string.IsNullOrWhiteSpace(CertificatePath)
        && !string.IsNullOrWhiteSpace(CertificatePassword);
}
