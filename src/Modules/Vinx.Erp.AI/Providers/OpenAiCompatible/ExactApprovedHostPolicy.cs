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

    private static string Normalize(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();
}
