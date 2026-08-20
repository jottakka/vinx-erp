using System.Net;
using System.Net.Sockets;
using Vinx.Erp.Foundation.Files.ClamAv;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.ClamAv;

public sealed class ClamAvDaemonClientTests
{
    private const int ChunkSize = 64 * 1024;
    private static readonly int[] expected = [1024, 1024];

    [Fact]
    public async Task Version_reply_parses_engine_and_signature_versions()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.CleanScan);
        await using var client = NewClient(daemon);

        var version = await client.ReadVersionAsync(CancellationToken.None);

        Assert.Equal(new ClamAvVersion("1.4.2", "27130"), version);
        var connection = await daemon.WaitForConnectionAsync(
            candidate => candidate.Command == "zVERSION", TimeSpan.FromSeconds(5));
        Assert.Equal("zVERSION", connection.Command);
    }

    [Fact]
    public async Task Scan_clean_content_returns_clean_result()
    {
        var payload = "clean document payload"u8.ToArray();
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.CleanScan);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(new MemoryStream(payload), 25 * 1024 * 1024, CancellationToken.None);

        Assert.True(result.Clean);
        Assert.Null(result.SignatureName);
        Assert.Null(result.ErrorCode);
        var connection = await daemon.WaitForConnectionAsync(
            candidate => candidate.Command == "zINSTREAM", TimeSpan.FromSeconds(5));
        Assert.True(connection.TerminatorSeen);
        Assert.False(connection.ClientAborted);
        Assert.Equal(payload, connection.Payload);
    }

    [Fact]
    public async Task Scan_found_content_returns_the_signature_name()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.FoundScan);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(
            new MemoryStream("suspicious payload"u8.ToArray()), 25 * 1024 * 1024, CancellationToken.None);

        Assert.False(result.Clean);
        Assert.Equal(FakeClamAvDaemon.TestSignature, result.SignatureName);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task Daemon_size_limit_error_maps_to_a_typed_error_code()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.DaemonSizeLimitExceeded);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(
            new MemoryStream(new byte[16]), 25 * 1024 * 1024, CancellationToken.None);

        Assert.False(result.Clean);
        Assert.Null(result.SignatureName);
        Assert.Equal(ClamAvScanResult.DaemonSizeLimitExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task Garbage_scan_reply_maps_to_a_malformed_error_code()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.GarbageScan);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(
            new MemoryStream(new byte[16]), 25 * 1024 * 1024, CancellationToken.None);

        Assert.False(result.Clean);
        Assert.Null(result.SignatureName);
        Assert.Equal(ClamAvScanResult.MalformedReply, result.ErrorCode);
    }

    [Fact]
    public async Task Garbage_version_reply_surfaces_a_protocol_error()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.GarbageVersion);
        await using var client = NewClient(daemon);

        await Assert.ThrowsAsync<ClamAvProtocolException>(
            () => client.ReadVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connection_refused_surfaces_the_connect_failure()
    {
        await using var client = new ClamAvDaemonClient("127.0.0.1", GetClosedPort());

        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scan_chunks_payload_in_64kib_big_endian_chunks_with_zero_terminator()
    {
        var payload = new byte[(2 * ChunkSize) + 18_928];
        new Random(42).NextBytes(payload);
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.CleanScan);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(new MemoryStream(payload), 25 * 1024 * 1024, CancellationToken.None);

        Assert.True(result.Clean);
        var connection = await daemon.WaitForConnectionAsync(
            candidate => candidate.Command == "zINSTREAM", TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { ChunkSize, ChunkSize, payload.Length - (2 * ChunkSize) }, connection.ChunkSizes);
        Assert.True(connection.TerminatorSeen);
        Assert.False(connection.ClientAborted);
        Assert.Equal(payload.Length, connection.TotalBytes);
        Assert.Equal(payload, connection.Payload);
    }

    [Fact]
    public async Task Client_size_limit_aborts_the_stream_without_terminator()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.CleanScan);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);

        var result = await client.ScanStreamAsync(
            new ChunkedSourceStream(totalBytes: 4096, bytesPerRead: 1024), maxBytes: 2048, CancellationToken.None);

        Assert.False(result.Clean);
        Assert.Equal(ClamAvScanResult.ClientSizeLimitExceeded, result.ErrorCode);
        var connection = await daemon.WaitForConnectionAsync(
            candidate => candidate.Command == "zINSTREAM", TimeSpan.FromSeconds(5));
        Assert.False(connection.TerminatorSeen);
        Assert.True(connection.ClientAborted);
        Assert.Equal(expected, connection.ChunkSizes);
        Assert.Equal(2048, connection.TotalBytes);
    }

    [Fact]
    public async Task External_cancellation_while_scanning_propagates()
    {
        using var daemon = new FakeClamAvDaemon(FakeClamAvScript.Silent);
        await using var client = NewClient(daemon);
        await client.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ScanStreamAsync(
                new MemoryStream(new byte[64]), 25 * 1024 * 1024, cancellation.Token));
    }

    private static ClamAvDaemonClient NewClient(FakeClamAvDaemon daemon) => new("127.0.0.1", daemon.Port);

    private static int GetClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Stream de teste que entrega payloads grandes em leituras fixas (para streaming em múltiplos chunks).</summary>
    private sealed class ChunkedSourceStream(int totalBytes, int bytesPerRead) : Stream
    {
        private readonly int bytesPerRead = bytesPerRead;
        private long remaining = totalBytes;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = (int)Math.Min(Math.Min(buffer.Length, bytesPerRead), remaining);
            remaining -= count;
            for (var index = 0; index < count; index++)
            {
                buffer.Span[index] = 0xAB;
            }

            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
    }
}
