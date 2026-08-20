using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vinx.Erp.Foundation.UnitTests.ClamAv;

/// <summary>Script determinístico executado pelo fake para cada comando recebido.</summary>
public enum FakeClamAvScript
{
    /// <summary>INSTREAM responde "stream: OK"; VERSION responde a versão fixa do fake.</summary>
    CleanScan,

    /// <summary>INSTREAM responde FOUND com a assinatura sintética do fake.</summary>
    FoundScan,

    /// <summary>INSTREAM responde FOUND, mas zVERSION responde bytes inválidos (fallback de versão).</summary>
    FoundScanGarbageVersion,

    /// <summary>INSTREAM responde o erro de tamanho do clamd ("INSTREAM size limit exceeded").</summary>
    DaemonSizeLimitExceeded,

    /// <summary>INSTREAM responde bytes sem formato de resposta válido.</summary>
    GarbageScan,

    /// <summary>VERSION responde bytes sem formato de versão válido.</summary>
    GarbageVersion,

    /// <summary>Aceita o comando e nunca responde (para cancelamento/timeout).</summary>
    Silent,
}

/// <summary>Registro de uma conexão tratada pelo fake: comando, framing INSTREAM e payload recebido.</summary>
public sealed record FakeClamAvConnection(
    string Command,
    bool TerminatorSeen,
    bool ClientAborted,
    IReadOnlyList<int> ChunkSizes,
    long TotalBytes,
    byte[] Payload);

/// <summary>
/// Servidor clamd falso in-process (TcpListener em porta efêmera de loopback) que valida o
/// protocolo recebido: comandos z NUL-terminados, chunks INSTREAM com prefixo de 4 bytes
/// big-endian e terminador 0x00000000. Sem bytes reais de malware; assinatura sintética.
/// </summary>
public sealed class FakeClamAvDaemon : IDisposable
{
    public const string VersionReply = "ClamAV 1.4.2/27130/2026-08-18";
    public const string TestSignature = "Test.Signature.Safe";

    private readonly TcpListener listener;
    private readonly Task acceptLoop;
    private readonly ConcurrentQueue<FakeClamAvConnection> recorded = new();
    private volatile bool stopped;

    public FakeClamAvDaemon(FakeClamAvScript script)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(() => AcceptConnectionsAsync(script));
    }

    public int Port { get; }

    public string Endpoint => $"127.0.0.1:{Port}";

    public IReadOnlyList<FakeClamAvConnection> Connections => new ReadOnlyCollection<FakeClamAvConnection>([.. recorded]);

    /// <summary>Aguarda o registro de uma conexão que satisfaça o predicado (o registro pode ocorrer após o reply).</summary>
    public async Task<FakeClamAvConnection> WaitForConnectionAsync(
        Func<FakeClamAvConnection, bool> match,
        TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var connection = recorded.ToArray().FirstOrDefault(match);
            if (connection is not null)
            {
                return connection;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException("Fake clamd did not record a matching connection.");
    }

    public void Dispose()
    {
        stopped = true;
        listener.Stop();
        try
        {
            acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Loop de accept já finalizado ou abortado pelo Stop — nada a fazer.
        }
    }

    private async Task AcceptConnectionsAsync(FakeClamAvScript script)
    {
        while (!stopped)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception exception) when (stopped
                || exception is InvalidOperationException
                || exception is ObjectDisposedException
                || exception is SocketException)
            {
                break;
            }

            await HandleConnectionAsync(client, script);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, FakeClamAvScript script)
    {
        using (client)
        {
            var command = string.Empty;
            var terminatorSeen = false;
            var clientAborted = false;
            List<int> chunkSizes = [];
            var payload = Array.Empty<byte>();
            try
            {
                var stream = client.GetStream();
                command = await ReadCommandAsync(stream);
                if (command == "zVERSION")
                {
                    if (script == FakeClamAvScript.Silent)
                    {
                        await WaitUntilDisconnectAsync(stream);
                    }
                    else
                    {
                        await WriteReplyAsync(
                            stream,
                            script is FakeClamAvScript.GarbageVersion or FakeClamAvScript.FoundScanGarbageVersion
                                ? "\u0001\u0002not-a-version"
                                : VersionReply,
                            nulTerminated: script is not (FakeClamAvScript.GarbageVersion
                                or FakeClamAvScript.FoundScanGarbageVersion));
                    }
                }
                else if (command == "zINSTREAM")
                {
                    var capture = await ReadInStreamAsync(stream);
                    terminatorSeen = capture.TerminatorSeen;
                    clientAborted = capture.ClientAborted;
                    chunkSizes = capture.ChunkSizes;
                    payload = capture.Payload;
                    if (!clientAborted)
                    {
                        if (script == FakeClamAvScript.Silent)
                        {
                            await WaitUntilDisconnectAsync(stream);
                        }
                        else
                        {
                            await WriteScanReplyAsync(stream, script);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                || exception is SocketException
                || exception is ObjectDisposedException)
            {
                clientAborted = command == "zINSTREAM" && !terminatorSeen;
            }
            finally
            {
                recorded.Enqueue(new FakeClamAvConnection(command, terminatorSeen, clientAborted, chunkSizes, payload.Length, payload));
            }
        }
    }

    private static async Task<string> ReadCommandAsync(NetworkStream stream)
    {
        var command = new MemoryStream();
        var single = new byte[1];
        while (command.Length < 32)
        {
            var read = await stream.ReadAsync(single.AsMemory());
            if (read == 0)
            {
                break;
            }

            if (single[0] == 0)
            {
                break;
            }

            command.WriteByte(single[0]);
        }

        return Encoding.ASCII.GetString(command.ToArray());
    }

    private static async Task<InStreamCapture> ReadInStreamAsync(NetworkStream stream)
    {
        var chunkSizes = new List<int>();
        var payload = new MemoryStream();
        while (true)
        {
            var prefix = await ReadExactAsync(stream, 4);
            if (prefix is null)
            {
                return new InStreamCapture(false, true, chunkSizes, payload.ToArray());
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
            if (length == 0)
            {
                return new InStreamCapture(true, false, chunkSizes, payload.ToArray());
            }

            var chunk = await ReadExactAsync(stream, length);
            if (chunk is null)
            {
                return new InStreamCapture(false, true, chunkSizes, payload.ToArray());
            }

            chunkSizes.Add(length);
            payload.Write(chunk);
        }
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private static Task WriteScanReplyAsync(NetworkStream stream, FakeClamAvScript script) => script switch
    {
        FakeClamAvScript.FoundScan or FakeClamAvScript.FoundScanGarbageVersion =>
            WriteReplyAsync(stream, $"stream: {TestSignature} FOUND", nulTerminated: true),
        FakeClamAvScript.DaemonSizeLimitExceeded =>
            WriteReplyAsync(stream, "INSTREAM size limit exceeded. ERROR", nulTerminated: true),
        FakeClamAvScript.GarbageScan => WriteReplyAsync(stream, "not-a-clamd-reply", nulTerminated: false),
        _ => WriteReplyAsync(stream, "stream: OK", nulTerminated: true),
    };

    private static async Task WriteReplyAsync(NetworkStream stream, string reply, bool nulTerminated)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(reply) + (nulTerminated ? 1 : 0)];
        Encoding.UTF8.GetBytes(reply, bytes);
        if (nulTerminated)
        {
            bytes[^1] = 0;
        }

        await stream.WriteAsync(bytes);
    }

    private static async Task WaitUntilDisconnectAsync(NetworkStream stream)
    {
        var probe = new byte[1];
        while (await stream.ReadAsync(probe.AsMemory()) > 0)
        {
            // Consome bytes extras (não deveria haver) até o cliente desconectar.
        }
    }

    private sealed record InStreamCapture(
        bool TerminatorSeen,
        bool ClientAborted,
        List<int> ChunkSizes,
        byte[] Payload);
}
