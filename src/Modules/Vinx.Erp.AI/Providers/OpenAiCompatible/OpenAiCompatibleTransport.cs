using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace Vinx.Erp.AI.Providers.OpenAiCompatible;

/// <summary>
/// Transporte dedicado de perfis OpenAI-compatible: bloqueia recursos HTTP que
/// alterariam o destino e revalida DNS antes de cada nova conexão.
/// </summary>
public static class OpenAiCompatibleTransport
{
    public static SocketsHttpHandler Create(
        OpenAiCompatibleOptions profile,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ValidateEndpoint(profile.Endpoint, profile);
        var resolver = resolve ?? ResolveAsync;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = (context, cancellationToken) => new ValueTask<Stream>(ConnectAsync(
                CreateTargetUri(context.DnsEndPoint),
                context.DnsEndPoint.Port,
                profile,
                resolver,
                cancellationToken)),
        };
    }

    /// <summary>
    /// Resolve e aprova todos os A/AAAA antes da conexão, rejeitando o resultado
    /// inteiro se qualquer endereço cair em faixa proibida.
    /// </summary>
    internal static async Task<IPAddress[]> ResolveApprovedAddressesAsync(
        Uri endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(resolve);

        cancellationToken.ThrowIfCancellationRequested();
        var addresses = await resolve(endpoint.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        var failures = ProviderEgressPolicy.ValidateResolvedAddresses(addresses);
        if (failures.Count > 0)
        {
            throw new ProviderEgressRejectedException("provider DNS result is not approved for egress");
        }

        return addresses;
    }

    private static async Task<Stream> ConnectAsync(
        Uri endpoint,
        int port,
        OpenAiCompatibleOptions profile,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpoint, profile);
        var addresses = await ResolveApprovedAddressesAsync(endpoint, resolve, cancellationToken).ConfigureAwait(false);
        Exception? lastFailure = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                socket.Dispose();
                lastFailure = exception;
            }
        }

        if (lastFailure is not null)
        {
            ExceptionDispatchInfo.Capture(lastFailure).Throw();
        }

        throw new HttpRequestException("provider DNS resolution returned no addresses");
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private static Uri CreateTargetUri(DnsEndPoint endpoint) =>
        new UriBuilder(Uri.UriSchemeHttps, endpoint.Host, endpoint.Port).Uri;

    private static void ValidateEndpoint(Uri? endpoint, OpenAiCompatibleOptions? profile)
    {
        var failures = ProviderEgressPolicy.ValidateEndpoint(
            endpoint,
            profile?.ApprovedHosts,
            profile?.ApprovedPort);
        if (failures.Count > 0)
        {
            throw new ProviderEgressRejectedException("provider endpoint is not approved for egress");
        }
    }
}
