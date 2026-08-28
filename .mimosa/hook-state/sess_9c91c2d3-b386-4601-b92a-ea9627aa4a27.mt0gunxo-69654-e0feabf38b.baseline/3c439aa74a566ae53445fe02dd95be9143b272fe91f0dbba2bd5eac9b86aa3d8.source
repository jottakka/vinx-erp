using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Vinx.Erp.Foundation.Bootstrap;

public sealed class FoundationBootstrapFeatureFlagsOptions
{
    public const string ConfigurationSectionName = "Foundation:Bootstrap";

    private static readonly Regex FlagKeyPattern = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public Dictionary<string, FoundationBootstrapFeatureFlagDefinitionOptions> Flags { get; set; } =
        new(StringComparer.Ordinal);

    public bool IsValid(out string failure)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, definition) in Flags)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                failure = "Foundation bootstrap feature flag key is invalid.";
                return false;
            }

            if (!keys.Add(key))
            {
                failure = "Foundation bootstrap feature flag keys must be unique without casing differences.";
                return false;
            }

            if (!FlagKeyPattern.IsMatch(key))
            {
                failure = "Foundation bootstrap feature flag key is invalid.";
                return false;
            }

            if (definition is null
                || definition.Enabled is null
                || string.IsNullOrWhiteSpace(definition.Scope)
                || string.IsNullOrWhiteSpace(definition.Owner)
                || string.IsNullOrWhiteSpace(definition.Reason)
                || definition.RemoveAfter is null)
            {
                failure = "Foundation bootstrap feature flag metadata is incomplete.";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    public IReadOnlyDictionary<string, bool> MaterializeEffectiveFlags() =>
        new ReadOnlyDictionary<string, bool>(
            Flags.ToDictionary(
                flag => flag.Key,
                flag => flag.Value.Enabled!.Value,
                StringComparer.Ordinal));
}

public sealed class FoundationBootstrapFeatureFlagDefinitionOptions
{
    public bool? Enabled { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateOnly? RemoveAfter { get; set; }
}
