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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            await client.ConnectAsync(
                options.Host,
                options.Port,
                options.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                timeout.Token
            );
            await client.AuthenticateAsync(options.Username, options.Password, timeout.Token);
            // Confirmou o 250 queued: entrega aceita pelo servidor. Daqui em
            // diante nenhuma falha pode virar retry (risco de duplicação).
            await client.SendAsync(mime, timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout do transporte antes da confirmação: resultado incerto no
            // provider vira ambíguo terminal, sem retry cego.
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
            // Falha transitiva de infraestrutura antes da confirmação (conexão
            // recusada, DNS, rede, 4xx temporário): delega o retry finito.
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

        // Após a confirmação do envio, a desconexão é apenas limpeza: falha aqui
        // não pode reenfileirar a mensagem já entregue. Tratamos como ambíguo
        // terminal (entrega confirmada, estado da sessão incerto).
        try
        {
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "SMTP email send {Operation} {Result} {Reason}",
                "smtp_email_send",
                "ambiguous",
                "smtp_disconnect_timeout"
            );
        }
        catch (Exception)
        {
            logger.LogWarning(
                "SMTP email send {Operation} {Result} {Reason}",
                "smtp_email_send",
                "ambiguous",
                "smtp_disconnect_failure"
            );
        }

        return new NotificationProviderResult(NotificationProviderResultKind.Delivered);
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
    /// Falhas de transporte (IO, socket, DNS) e respostas SMTP temporárias (4xx)
    /// são transitivas; autenticação, conteúdo inválido e 5xx definitivo não
    /// estão nesta lista.
    /// </summary>
    private static bool IsTransientFailure(Exception exception)
    {
        if (
            exception is System.Net.Sockets.SocketException
                or IOException
                or System.Net.Http.HttpRequestException
                or MailKit.ServiceNotConnectedException
        )
        {
            return true;
        }

        // Resposta SMTP temporária (4xx) é transitiva; 5xx definitivo não.
        return exception is SmtpCommandException command
            && (int)command.StatusCode is >= 400 and < 500;
    }
}

/// <summary>
/// Mensagem pronta para envio; os campos vêm da fronteira estreita (tentativa
/// em curso), nunca de entrada livre.
/// </summary>
public sealed record EmailMessage(string Recipient, string Subject, string BodyHtml);
