using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Servidor SMTP falso em loopback sem TLS para os testes de entrega de e-mail:
/// fala o mínimo do protocolo (EHLO/HELO, AUTH LOGIN, MAIL FROM, RCPT TO, DATA,
/// QUIT) e captura a mensagem final para asserção. Nenhum tráfego externo.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<CapturedMail> _mails = [];
    private readonly Lock _gate = new();

    private Task? _acceptLoop;

    public FakeSmtpServer(string username, string password)
    {
        Username = username;
        Password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public string Username { get; }

    public string Password { get; }

    public bool RejectAuthentication { get; set; }

    public IReadOnlyList<CapturedMail> Mails
    {
        get
        {
            lock (_gate)
            {
                return _mails.ToList();
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            await WriteAsync(stream, "220 fake-smtp ready\r\n");
            var authenticated = false;
            CapturedMail? mail = null;

            while (true)
            {
                var line = await ReadLineAsync(stream);
                if (line is null)
                {
                    break;
                }

                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                {
                    await WriteAsync(stream, "250-fake-smtp\r\n250 AUTH LOGIN\r\n");
                }
                else if (upper.StartsWith("AUTH LOGIN"))
                {
                    await WriteAsync(stream, "334 VXNlcm5hbWU6\r\n");
                    var userB64 = await ReadLineAsync(stream);
                    await WriteAsync(stream, "334 UGFzc3dvcmQ6\r\n");
                    var passB64 = await ReadLineAsync(stream);
                    authenticated = RejectAuthentication
                        ? false
                        : DecodeBase64(userB64) == Username && DecodeBase64(passB64) == Password;
                    await WriteAsync(
                        stream,
                        authenticated ? "235 2.7.0 authenticated\r\n" : "535 5.7.8 authentication failed\r\n"
                    );
                    if (!authenticated)
                    {
                        return;
                    }
                }
                else if (upper.StartsWith("MAIL FROM"))
                {
                    await WriteAsync(stream, "250 2.1.0 ok\r\n");
                }
                else if (upper.StartsWith("RCPT TO"))
                {
                    var recipient = ExtractAngle(line, "RCPT TO:");
                    mail = new CapturedMail { Recipient = recipient };
                    await WriteAsync(stream, "250 2.1.5 ok\r\n");
                }
                else if (upper.StartsWith("DATA"))
                {
                    await WriteAsync(stream, "354 end with .\r\n");
                    var data = await ReadDataAsync(stream);
                    if (mail is not null)
                    {
                        mail.Data = data;
                        lock (_gate)
                        {
                            _mails.Add(mail);
                        }
                    }
                    await WriteAsync(stream, "250 2.0.0 queued\r\n");
                }
                else if (upper.StartsWith("QUIT"))
                {
                    await WriteAsync(stream, "221 2.0.0 bye\r\n");
                    return;
                }
                else if (upper.StartsWith("RSET"))
                {
                    await WriteAsync(stream, "250 2.0.0 reset\r\n");
                }
                else
                {
                    await WriteAsync(stream, "250 2.0.0 ok\r\n");
                }
            }
        }
    }

    private static string DecodeBase64(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string? ExtractAngle(string line, string prefix)
    {
        var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = line.IndexOf('<', index);
        var end = line.IndexOf('>', start + 1);
        return start >= 0 && end > start ? line[(start + 1)..end] : null;
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                return null;
            }

            if (b == 10)
            {
                break;
            }

            if (b != 13)
            {
                buffer.Append((char)b);
            }
        }

        return buffer.ToString();
    }

    private static async Task<string> ReadDataAsync(NetworkStream stream)
    {
        var buffer = new StringBuilder();
        string? previous = null;
        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (line == ".")
            {
                break;
            }

            if (previous is not null)
            {
                buffer.Append(previous).Append('\n');
            }

            previous = line;
        }

        return buffer.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}

public sealed class CapturedMail
{
    public string? Recipient { get; set; }

    public string? Data { get; set; }
}
