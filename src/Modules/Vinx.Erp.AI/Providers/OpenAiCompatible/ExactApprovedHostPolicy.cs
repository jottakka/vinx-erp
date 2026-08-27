namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>Valida uma lista não vazia cujo host normalizado corresponde exatamente ao endpoint.</summary>
public static class ExactApprovedHostPolicy
{
    public static bool Matches(Uri? endpoint, IReadOnlyList<string>? approvedHosts)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || approvedHosts is null
            || approvedHosts.Count == 0
            || approvedHosts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var endpointHost = Normalize(endpoint.DnsSafeHost);
        return endpointHost.Length > 0 && approvedHosts.Any(host =>
            string.Equals(endpointHost, Normalize(host), StringComparison.Ordinal));
    }

    /// <summary>Compara endpoint canônico e conjunto exato de hosts normalizados.</summary>
    public static bool ProfilesMatch(
        Uri? firstEndpoint,
        IReadOnlyList<string>? firstApprovedHosts,
        Uri? secondEndpoint,
        IReadOnlyList<string>? secondApprovedHosts)
    {
        if (firstEndpoint is null
            || secondEndpoint is null
            || !firstEndpoint.IsAbsoluteUri
            || !secondEndpoint.IsAbsoluteUri
            || !TryNormalizeHosts(firstApprovedHosts, out var firstHosts)
            || !TryNormalizeHosts(secondApprovedHosts, out var secondHosts))
        {
            return false;
        }

        return Uri.Compare(
                firstEndpoint,
                secondEndpoint,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) == 0
            && Uri.Compare(
                firstEndpoint,
                secondEndpoint,
                UriComponents.UserInfo | UriComponents.PathAndQuery | UriComponents.Fragment,
                UriFormat.UriEscaped,
                StringComparison.Ordinal) == 0
            && firstHosts.SetEquals(secondHosts);
    }

    private static bool TryNormalizeHosts(
        IReadOnlyList<string>? approvedHosts,
        out HashSet<string> normalized)
    {
        normalized = [];
        if (approvedHosts is null
            || approvedHosts.Count == 0
            || approvedHosts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        normalized.UnionWith(approvedHosts.Select(Normalize));
        return normalized.Count > 0;
    }

    private static string Normalize(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();
}
