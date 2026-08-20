using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Vinx.Erp.Foundation.Files.ClamAv;

/// <summary>Erro de protocolo: o clamd respondeu algo fora do formato esperado (nunca exceção de rede).</summary>
internal sealed class ClamAvProtocolException(string message) : Exception(message);

/// <summary>Versão reportada pelo clamd via zVERSION: engine (ex.: "1.4.2") e base de assinaturas (ex.: "27130").</summary>
internal sealed record ClamAvVersion(string EngineVersion, string SignatureVersion);

/// <summary>Resultado tipado do scan INSTREAM; erros de protocolo/tamanho viram <see cref="ErrorCode"/>, nunca exceção.</summary>
internal sealed record ClamAvScanResult(bool Clean, string? SignatureName, string? ErrorCode)
{
    /// <summary>Erro de tamanho reportado pelo próprio clamd ("INSTREAM size limit exceeded").</summary>
    public const string DaemonSizeLimitExceeded = "instream_size_limit_exceeded";

    /// <summary>Limite local (<see cref="MalwareScannerOptions.MaxScanBytes"/>) excedido durante o streaming.</summary>
    public const string ClientSizeLimitExceeded = "client_size_limit_exceeded";

    /// <summary>Resposta do daemon sem formato reconhecível.</summary>
    public const string MalformedReply = "malformed_reply";
}

/// <summary>
/// Cliente mínimo do protocolo clamd sobre TCP (docs.clamav.net, "ClamD Protocol"): comandos z
/// NUL-terminados; zINSTREAM com chunks de 4 bytes big-endian de comprimento + dados (64 KiB por
/// chunk) e terminador 0x00000000; respostas "stream: OK", "stream: SIGNATURE FOUND" ou erro de
/// tamanho. Como o clamd encerra a conexão após responder um comando não-sessão, cada comando usa
/// a própria conexão: <see cref="ConnectAsync"/> reserva a conexão do scan e <see cref="ReadVersionAsync"/>
/// abre uma nova. Falhas de conexão/IO propagam como exceções de socket; falhas de formato de
/// resposta do scan viram <see cref="ClamAvScanResult.ErrorCode"/> e de versão viram
/// <see cref="ClamAvProtocolException"/>.
/// </summary>
internal sealed class ClamAvDaemonClient(string host, int port) : IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxReplyBytes = 1024;

    private static readonly byte[] StreamTerminator = [0, 0, 0, 0];

    private readonly byte[] chunkPrefix = new byte[4];
    private TcpClient? scanConnection;

    /// <summary>Conecta ao clamd e reserva a conexão para o próximo <see cref="ScanStreamAsync"/>.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        scanConnection?.Dispose();
        scanConnection = client;
    }

    /// <summary>
    /// Envia o conteúdo via zINSTREAM com chunks big-endian de 64 KiB. Exceder <paramref name="maxBytes"/>
    /// aborta a conexão SEM terminador (o daemon não recebe o resto) e retorna o erro tipado local.
    /// </summary>
    public async Task<ClamAvScanResult> ScanStreamAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        var client = scanConnection;
        if (client is null)
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
        }

        scanConnection = null;
        try
        {
            var stream = client.GetStream();
            await SendCommandAsync(stream, "zINSTREAM", cancellationToken);
            var buffer = new byte[ChunkSize];
            long streamed = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                if (streamed + read > maxBytes)
                {
                    // Abort: fecha a conexão sem terminador — o clamd não deve receber bytes além do limite.
                    client.Dispose();
                    return new ClamAvScanResult(false, null, ClamAvScanResult.ClientSizeLimitExceeded);
                }

                BinaryPrimitives.WriteInt32BigEndian(chunkPrefix, read);
                await stream.WriteAsync(chunkPrefix, cancellationToken);
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                streamed += read;
            }

            await stream.WriteAsync(StreamTerminator, cancellationToken);
            var reply = await ReadReplyAsync(stream, cancellationToken);
            return ParseScanReply(reply);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>zVERSION em conexão própria; resposta "ClamAV &lt;engine&gt;/&lt;assinaturas&gt;[/data]".</summary>
    public async Task<ClamAvVersion> ReadVersionAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var stream = client.GetStream();
        await SendCommandAsync(stream, "zVERSION", cancellationToken);
        var reply = await ReadReplyAsync(stream, cancellationToken);
        return ParseVersionReply(reply);
    }

    public ValueTask DisposeAsync()
    {
        scanConnection?.Dispose();
        scanConnection = null;
        return ValueTask.CompletedTask;
    }

    private static async Task SendCommandAsync(NetworkStream stream, string command, CancellationToken cancellationToken)
    {
        // Comando z: bytes do comando + terminador NUL (mantido pelo tamanho do array).
        var bytes = new byte[command.Length + 1];
        Encoding.ASCII.GetBytes(command, bytes);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var reply = new MemoryStream();
        while (reply.Length < MaxReplyBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var terminator = Array.IndexOf(buffer, (byte)0, 0, read);
            if (terminator >= 0)
            {
                reply.Write(buffer, 0, terminator);
                break;
            }

            reply.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(reply.ToArray());
    }

    private static ClamAvScanResult ParseScanReply(string reply)
    {
        if (reply == "stream: OK")
        {
            return new ClamAvScanResult(true, null, null);
        }

        if (reply.StartsWith("stream: ", StringComparison.Ordinal)
            && reply.EndsWith(" FOUND", StringComparison.Ordinal)
            && reply.Length > "stream:  FOUND".Length)
        {
            return new ClamAvScanResult(false, reply["stream: ".Length..^" FOUND".Length], null);
        }

        if (reply.Contains("size limit exceeded", StringComparison.Ordinal))
        {
            return new ClamAvScanResult(false, null, ClamAvScanResult.DaemonSizeLimitExceeded);
        }

        return new ClamAvScanResult(false, null, ClamAvScanResult.MalformedReply);
    }

    private static ClamAvVersion ParseVersionReply(string reply)
    {
        const string prefix = "ClamAV ";
        if (!reply.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ClamAvProtocolException($"Unexpected clamd VERSION reply: {reply.Length} bytes.");
        }

        var segments = reply[prefix.Length..].Split('/');
        if (segments.Length < 2
            || string.IsNullOrWhiteSpace(segments[0])
            || string.IsNullOrWhiteSpace(segments[1]))
        {
            throw new ClamAvProtocolException($"Unparseable clamd VERSION reply: {reply.Length} bytes.");
        }

        return new ClamAvVersion(segments[0].Trim(), segments[1].Trim());
    }
}
