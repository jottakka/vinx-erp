using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Notifications;

namespace Vinx.Erp.Notifications.Providers.Email;

/// <summary>
/// Transporte SMTP do canal e-mail: apenas classifica o resultado — retry e
/// DLQ pertencem ao contrato do worker (#48), nunca a este adapter. Timeout e
/// conexão interrompida viram ambíguo terminal; recusa de autenticação/endereço
/// é rejeição definitiva; falha transitiva de infraestrutura propaga para o
/// retry finito do worker.
/// </summary>
public sealed class SmtpEmailClient(EmailProviderOptions options, ILogger logger)
{
    /// <summary>
    /// Envia a mensagem e classifica o resultado. Nunca registra destinatário,
    /// assunto ou corpo; somente o resultado estável e o motivo seguro.
    /// </summary>
    public async Task<NotificationProviderResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken
    )
    {
        using var mime = BuildMime(message);
        using var client = new SmtpClient();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            await client.ConnectAsync(
                options.Host,
                options.Port,
                options.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                timeout.Token
            );
            await client.AuthenticateAsync(options.Username, options.Password, timeout.Token);
            await client.SendAsync(mime, timeout.Token);
            await client.DisconnectAsync(true, CancellationToken.None);

            return new NotificationProviderResult(NotificationProviderResultKind.Delivered);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout do transporte: resultado incerto no provider vira
            // ambíguo terminal, sem retry cego.
            logger.LogWarning(
                "SMTP email send {Operation} {Result} {Reason}",
                "smtp_email_send",
                "ambiguous",
                "smtp_timeout"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Ambiguous);
        }
        catch (AuthenticationException)
        {
            // Credenciais rejeitadas: definitivo, sem retry.
            logger.LogWarning(
                "SMTP email send {Operation} {Result} {Reason}",
                "smtp_email_send",
                "rejected",
                "smtp_authentication"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }
        catch (Exception exception)
            when (IsTransientFailure(exception))
        {
            // Falha transitiva de infraestrutura (conexão recusada, DNS, rede):
            // delega o retry finito ao worker.
            throw new NotificationProviderTransientException(
                "SMTP email transport reported a transient failure."
            );
        }
        catch (Exception)
        {
            // Qualquer outro erro (mensagem inválida, endereço recusado) é
            // classificado como rejeição definitiva sem retry cego.
            logger.LogWarning(
                "SMTP email send {Operation} {Result} {Reason}",
                "smtp_email_send",
                "rejected",
                "smtp_rejected"
            );
            return new NotificationProviderResult(NotificationProviderResultKind.Rejected);
        }
    }

    private MimeMessage BuildMime(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(new MailboxAddress(string.Empty, message.Recipient));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { HtmlBody = message.BodyHtml };
        mime.Body = body.ToMessageBody();
        return mime;
    }

    /// <summary>
    /// Falhas de transporte (IO, socket, DNS, TLS handshake) são transitivas;
    /// autenticação e validação de conteúdo não estão nesta lista.
    /// </summary>
    private static bool IsTransientFailure(Exception exception) =>
        exception is System.Net.Sockets.SocketException or IOException
            or System.Net.Http.HttpRequestException;
}

/// <summary>
/// Mensagem pronta para envio; os campos vêm da fronteira estreita (tentativa
/// em curso), nunca de entrada livre.
/// </summary>
public sealed record EmailMessage(string Recipient, string Subject, string BodyHtml);
