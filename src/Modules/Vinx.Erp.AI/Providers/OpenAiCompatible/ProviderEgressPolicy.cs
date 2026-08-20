using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Endpoint rejeitado pela política de egress: a URI configurada não é um
/// destino aprovado (esquema, porta, host ou faixa de endereço proibida).
/// </summary>
public sealed class ProviderEgressRejectedException(string message) : Exception(message);

/// <summary>
/// Política pura de egress para endpoints OpenAI-compatible: valida esquema,
/// porta e host (hostname ou literal IP) sem I/O de rede. A validação por
/// endereço resolvido é exposta para o transporte do host validar cada
/// conexão (defesa contra DNS rebinding), na fatia da API (#138).
/// </summary>
public static class ProviderEgressPolicy
{
    public const int DefaultHttpsPort = 443;

    private static readonly (byte[] Network, int PrefixLength)[] ProhibitedNetworks = CreateProhibitedNetworks();

    /// <summary>
    /// Valida a URI do endpoint. Sem I/O de rede: literais IP e hostnames são
    /// checados por sintaxe/allowlist; a resolução DNS é responsabilidade do
    /// transporte no momento da conexão (<see cref="ValidateResolvedAddresses"/>).
    /// </summary>
    public static IReadOnlyList<string> ValidateEndpoint(
        Uri? endpoint,
        IReadOnlyList<string>? approvedHosts = null,
        int? approvedPort = null)
    {
        var failures = new List<string>();

        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "https")
        {
            failures.Add("endpoint must be absolute https");
            return failures;
        }

        if (endpoint.Port != DefaultHttpsPort && endpoint.Port != approvedPort)
        {
            failures.Add("endpoint port must be 443 or the explicitly approved port");
        }

        var host = NormalizeHost(endpoint.Host);
        if (host.Length == 0)
        {
            failures.Add("endpoint host is required");
            return failures;
        }

        if (host == "localhost")
        {
            failures.Add("endpoint host localhost is not allowed");
            return failures;
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (IsProhibitedAddress(literalAddress))
            {
                failures.Add("endpoint IP literal is in a prohibited range");
            }

            return failures;
        }

        if (UsesNonStandardNumericIpSpelling(host))
        {
            // Grafias que o resolver aceita (hex/octal/quad parcial) e que
            // escapam do IPAddress.TryParse restrito.
            failures.Add("endpoint host uses a non-standard numeric IP spelling");
            return failures;
        }

        var approved = approvedHosts?
            .Select(NormalizeHost)
            .Where(entry => entry.Length > 0)
            .ToArray();
        if (approved is { Length: > 0 } && !approved.Any(entry => MatchesApprovedHost(host, entry)))
        {
            failures.Add("endpoint host is not in the approved host list");
        }

        return failures;
    }

    /// <summary>
    /// Endereço é proibido quando está em faixa loopback, privada, reservada,
    /// link-local, CGNAT, multicast, documentação, unspecified ou
    /// IPv4-mapped equivalente. Sem I/O de rede.
    /// </summary>
    public static bool IsProhibitedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && IsIPv4Mapped(bytes))
        {
            address = address.MapToIPv4();
            bytes = address.GetAddressBytes();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        return ProhibitedNetworks.Any(network =>
            network.Network.Length == bytes.Length &&
            PrefixMatches(bytes, network.Network, network.PrefixLength));
    }

    // IPv4-mapped IPv6: 80 bits zero + `ffff` antes dos 4 bytes IPv4.
    private static bool IsIPv4Mapped(byte[] addressBytes) =>
        addressBytes.Length == 16
        && addressBytes.AsSpan(0, 10).IndexOfAnyExcept((byte)0) < 0
        && addressBytes[10] == 0xFF
        && addressBytes[11] == 0xFF;

    /// <summary>
    /// Valida todos os endereços de uma resolução DNS (A/AAAA). Rejeita se
    /// qualquer endereço cair em faixa proibida; a mensagem é fixa e não ecoa
    /// o endereço resolvido.
    /// </summary>
    public static IReadOnlyList<string> ValidateResolvedAddresses(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        return addresses.Any(IsProhibitedAddress)
            ? ["resolved address is in a prohibited range"]
            : [];
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();

    // 0x7f000001 (hex), 0177.0.0.1 (octal), 10.1 e 12345 (quad parcial/inteiro)
    // são aceitos por resolvers estilo inet_aton, resolvem para IP proibido e
    // não são hostnames reais: rejeitados como grafia numérica não padrão.
    private static bool UsesNonStandardNumericIpSpelling(string host) =>
        host.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        || (host.All(c => c is >= '0' and <= '9' or '.')
            && !IPAddress.TryParse(host, out _));

    /// <summary>
    /// Match por host exato ou sufixo com fronteira de ponto: aprova
    /// subdomínios diretos do aprovado, nunca um sufixo embutido em outro
    /// domínio (`EndsWith` ingênuo é proibido).
    /// </summary>
    private static bool MatchesApprovedHost(string host, string entry) =>
        host == entry || host.EndsWith("." + entry, StringComparison.Ordinal);

    private static (byte[] Network, int PrefixLength)[] CreateProhibitedNetworks()
    {
        string[] prohibited =
        [
            // IPv4
            "0.0.0.0/8",       // this-network
            "10.0.0.0/8",      // privada RFC1918
            "100.64.0.0/10",   // CGNAT compartilhada RFC6598
            "127.0.0.0/8",     // loopback
            "169.254.0.0/16",  // link-local (inclui 169.254.169.254)
            "172.16.0.0/12",   // privada RFC1918
            "192.0.2.0/24",    // documentação TEST-NET-1
            "192.168.0.0/16",  // privada RFC1918
            "198.18.0.0/15",   // benchmark RFC2544
            "198.51.100.0/24", // documentação TEST-NET-2
            "203.0.113.0/24",  // documentação TEST-NET-3
            "224.0.0.0/4",     // multicast
            "240.0.0.0/4",     // reservada (classe E + broadcast)
            // IPv6
            "fc00::/7",        // ULA privada
            "fe80::/10",       // link-local
            "ff00::/8",        // multicast
            "2001:db8::/32",   // documentação
        ];

        return [.. prohibited.Select(ParseNetwork)];

        static (byte[] Network, int PrefixLength) ParseNetwork(string cidr)
        {
            var separator = cidr.IndexOf('/', StringComparison.Ordinal);
            return (
                IPAddress.Parse(cidr[..separator]).GetAddressBytes(),
                int.Parse(cidr[(separator + 1)..], CultureInfo.InvariantCulture));
        }
    }

    private static bool PrefixMatches(byte[] address, byte[] network, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainderBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i])
            {
                return false;
            }
        }

        if (remainderBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainderBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }
}
