using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Files;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Persistence;
using Vinx.Erp.Notifications.Providers.Meta;
using Vinx.Erp.Notifications.Providers.Telegram;

namespace Vinx.Erp.Notifications;

/// <summary>
/// Adapter concreto de canais duráveis para o núcleo compartilhado. O envelope
/// permanece opaco; endereço e conteúdo são relidos somente após claim autorizado.
/// </summary>
public sealed class InboundAgentChannelAdapter(
    FoundationDbContext database,
    AgentExecutionService executionService,
    InboundAttachmentMaterializer attachmentMaterializer,
    IHttpClientFactory httpClientFactory,
    MetaWhatsAppOptions whatsAppOptions,
    TelegramOptions telegramOptions,
    ILogger<InboundAgentChannelAdapter> logger)
{
    private const string Operation = "inbound_agent_channel";
    private const string Module = "notifications";
    private const int MaximumReplyCharacters = 800;

    public async Task ProcessAsync(
        InboundAgentRequestReceived envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.InboundMessageId == Guid.Empty
            || envelope.Channel is not (AgentChannelKind.WhatsApp or AgentChannelKind.Telegram))
        {
            throw new InboundWhatsAppPermanentException("Inbound agent envelope is invalid.");
        }

        if (envelope.Channel == AgentChannelKind.Telegram)
        {
            await ProcessTelegramAsync(envelope.InboundMessageId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var claim = await ClaimWhatsAppAsync(envelope.InboundMessageId, cancellationToken)
            .ConfigureAwait(false);
        if (!claim.Claimed)
        {
            logger.LogInformation(
                "Inbound agent {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "skipped",
                claim.Status);
            return;
        }

        var actor = new ActorContext(claim.UserId, Guid.NewGuid());
        IReadOnlyList<Guid> attachments = [];
        if (claim.MessageType is "image" or "document")
        {
            if (claim.IsTerminalRecovery)
            {
                if (claim.AgentFileId is not { } recoveredFileId)
                {
                    return;
                }

                attachments = [recoveredFileId];
            }
            else
            {
                InboundAttachmentMaterializationResult materialized;
                try
                {
                    materialized = await MaterializeWhatsAppAttachmentAsync(
                        claim,
                        actor,
                        envelope.InboundMessageId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (NotificationProviderTransientException)
                {
                    await ReleaseAsync(envelope.InboundMessageId, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                if (!materialized.IsConfirmed)
                {
                    await FinalizeAsync(
                        envelope.InboundMessageId,
                        "failed",
                        materialized.FailureReason ?? "document_rejected",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                attachments = [materialized.ConfirmedFileId!.Value];
                if (!await ExecuteBooleanFunctionAsync(
                        "SELECT notifications.record_inbound_whatsapp_agent_file(@inbound_id, @file_id)",
                        cancellationToken,
                        ("inbound_id", envelope.InboundMessageId),
                        ("file_id", materialized.ConfirmedFileId.Value)).ConfigureAwait(false))
                {
                    await FinalizeAsync(
                        envelope.InboundMessageId,
                        "failed",
                        "agent_failed",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
        }

        var command = new AgentCommand(
            actor,
            Channel: ToAgentCommandChannel(envelope.Channel),
            InboundMessageId: envelope.InboundMessageId,
            RequestId: envelope.InboundMessageId,
            Journey: claim.MessageType == "document"
                ? AgentTaskProfileCatalog.PaguaiFromDocumentProfileId
                : AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            Message: ComposeMessage(claim),
            CompanyId: claim.CompanyId,
            Attachments: attachments);

        AgentExecutionResult execution;
        try
        {
            execution = await executionService.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            logger.LogError(
                exception,
                "Inbound agent {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "ambiguous",
                "execution_unconfirmed");
            return;
        }

        if (claim.IsTerminalRecovery && execution.Disposition == AgentExecutionDisposition.Conflict)
        {
            return;
        }

        if (execution.Disposition is AgentExecutionDisposition.InProgress
            or AgentExecutionDisposition.Ambiguous)
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (execution.Disposition is not AgentExecutionDisposition.Completed)
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "failed",
                "agent_failed",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(whatsAppOptions.WebAppBaseUrl, UriKind.Absolute, out var webAppBaseUri))
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "failed",
                "agent_failed",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var reply = MapReply(execution, webAppBaseUri);
        if (reply is null)
        {
            await FinalizeAsync(
                envelope.InboundMessageId,
                "completed",
                null,
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await SendWhatsAppReplyAsync(
            envelope.InboundMessageId,
            claim.WaId,
            reply,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mapeia somente resultado já liberado pelo núcleo. Proposta Pagaí ignora
    /// integralmente o texto do modelo e usa rota Web fixa gerada no servidor.
    /// </summary>
    public static string? MapReply(AgentExecutionResult result, Uri webAppBaseUri)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(webAppBaseUri);
        if (!webAppBaseUri.IsAbsoluteUri
            || webAppBaseUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(webAppBaseUri.UserInfo))
        {
            throw new ArgumentException("The Web review origin must be an absolute HTTPS URI.", nameof(webAppBaseUri));
        }

        if (result.Disposition != AgentExecutionDisposition.Completed)
        {
            return null;
        }

        if (result.PaguaiProposalId is { } proposalId)
        {
            var baseText = webAppBaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? webAppBaseUri.AbsoluteUri
                : webAppBaseUri.AbsoluteUri + "/";
            var review = new Uri(
                new Uri(baseText, UriKind.Absolute),
                $"paguai?proposal={proposalId:D}");
            return $"Documento pronto para revisão: {review.AbsoluteUri}";
        }

        var text = result.Outcome?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= MaximumReplyCharacters
            ? text
            : text[..(MaximumReplyCharacters - 1)] + "…";
    }

    private async Task ProcessTelegramAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        var claim = await ClaimTelegramAsync(inboundMessageId, cancellationToken)
            .ConfigureAwait(false);
        if (!claim.Claimed)
        {
            logger.LogInformation(
                "Inbound agent {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "skipped",
                claim.Status);
            return;
        }

        var actor = new ActorContext(claim.UserId, Guid.NewGuid());
        IReadOnlyList<Guid> attachments = [];
        if (claim.MessageType is "photo" or "document")
        {
            if (claim.IsTerminalRecovery)
            {
                if (claim.AgentFileId is not { } recoveredFileId)
                {
                    return;
                }

                attachments = [recoveredFileId];
            }
            else
            {
                InboundAttachmentMaterializationResult materialized;
                try
                {
                    var media = await new TelegramMediaClient(
                            httpClientFactory.CreateClient(TelegramClient.HttpClientName),
                            telegramOptions)
                        .DownloadAsync(
                            claim.MediaId!,
                            claim.MediaType!,
                            checked((int)DocumentUploadPolicy.MaxSizeBytes),
                            cancellationToken)
                        .ConfigureAwait(false);
                    materialized = await attachmentMaterializer.MaterializeAsync(
                        new InboundAttachmentMaterializationRequest(
                            inboundMessageId,
                            actor,
                            claim.CompanyId,
                            claim.MembershipId,
                            media.MediaType,
                            media.Bytes),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (NotificationProviderTransientException)
                {
                    await ReleaseTelegramAsync(inboundMessageId, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                catch (InboundTelegramMediaException)
                {
                    materialized = InboundAttachmentMaterializationResult.Failed("media_unavailable");
                }

                if (!materialized.IsConfirmed)
                {
                    await FinalizeTelegramAsync(
                        inboundMessageId,
                        "failed",
                        materialized.FailureReason ?? "document_rejected",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                attachments = [materialized.ConfirmedFileId!.Value];
                if (!await ExecuteBooleanFunctionAsync(
                        "SELECT notifications.record_inbound_telegram_agent_file(@inbound_id, @file_id)",
                        cancellationToken,
                        ("inbound_id", inboundMessageId),
                        ("file_id", materialized.ConfirmedFileId.Value)).ConfigureAwait(false))
                {
                    await FinalizeTelegramAsync(
                        inboundMessageId,
                        "failed",
                        "agent_failed",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
        }

        var command = new AgentCommand(
            actor,
            Channel: ToAgentCommandChannel(AgentChannelKind.Telegram),
            InboundMessageId: inboundMessageId,
            RequestId: inboundMessageId,
            Journey: claim.MessageType == "document"
                ? AgentTaskProfileCatalog.PaguaiFromDocumentProfileId
                : AgentTaskProfileCatalog.ReadOnlySummarizeProfileId,
            Message: ComposeTelegramMessage(claim),
            CompanyId: claim.CompanyId,
            Attachments: attachments);

        AgentExecutionResult execution;
        try
        {
            execution = await executionService.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinalizeTelegramAsync(
                inboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await FinalizeTelegramAsync(
                inboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            logger.LogError(
                exception,
                "Inbound agent {Operation} {Module} {Result} {Reason}",
                Operation,
                Module,
                "ambiguous",
                "execution_unconfirmed");
            return;
        }

        if (claim.IsTerminalRecovery && execution.Disposition == AgentExecutionDisposition.Conflict)
        {
            return;
        }

        if (execution.Disposition is AgentExecutionDisposition.InProgress
            or AgentExecutionDisposition.Ambiguous)
        {
            await FinalizeTelegramAsync(
                inboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (execution.Disposition is not AgentExecutionDisposition.Completed)
        {
            await FinalizeTelegramAsync(
                inboundMessageId,
                "failed",
                "agent_failed",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var reply = MapTelegramReply(execution);
        if (reply is null)
        {
            await FinalizeTelegramAsync(inboundMessageId, "completed", null, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await EnqueueTelegramReplyAsync(
            inboundMessageId,
            reply.Value.Kind,
            reply.Value.Text,
            reply.Value.DeepLinkSelector,
            CancellationToken.None).ConfigureAwait(false);
        await FinalizeTelegramAsync(inboundMessageId, "completed", null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static (string Kind, string Text, string? DeepLinkSelector)? MapTelegramReply(
        AgentExecutionResult result)
    {
        if (result.PaguaiProposalId is { } proposalId)
        {
            return (
                "paguai_review",
                "Documento pronto para revisão",
                $"/paguai?proposal={proposalId:D}");
        }

        var text = result.Outcome?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (
            "result",
            text.Length <= MaximumReplyCharacters
                ? text
                : text[..(MaximumReplyCharacters - 1)] + "…",
            null);
    }

    private async Task<InboundAttachmentMaterializationResult> MaterializeWhatsAppAttachmentAsync(
        WhatsAppClaim claim,
        ActorContext actor,
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        MetaWhatsAppMedia media;
        try
        {
            var client = new MetaWhatsAppMediaClient(
                httpClientFactory.CreateClient(MetaWhatsAppClient.HttpClientName),
                whatsAppOptions,
                logger);
            media = await client.DownloadAsync(
                claim.MediaId!,
                DocumentUploadPolicy.MaxSizeBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InboundWhatsAppPermanentException)
        {
            return InboundAttachmentMaterializationResult.Failed("media_unavailable");
        }

        return await attachmentMaterializer.MaterializeAsync(
            new InboundAttachmentMaterializationRequest(
                inboundMessageId,
                actor,
                claim.CompanyId,
                claim.MembershipId,
                media.MediaType,
                media.Bytes),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendWhatsAppReplyAsync(
        Guid inboundMessageId,
        string waId,
        string body,
        CancellationToken cancellationToken)
    {
        if (!whatsAppOptions.OutboundIsConfigured)
        {
            await FinalizeAsync(
                inboundMessageId,
                "failed",
                "agent_failed",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (!await ExecuteBooleanFunctionAsync(
                "SELECT notifications.begin_inbound_whatsapp_reply(@inbound_id)",
                cancellationToken,
                ("inbound_id", inboundMessageId)).ConfigureAwait(false))
        {
            return;
        }

        var client = new MetaWhatsAppClient(
            httpClientFactory.CreateClient(MetaWhatsAppClient.HttpClientName),
            whatsAppOptions,
            logger);
        MetaWhatsAppSendResult send;
        try
        {
            send = await client.SendTextAsync(
                MetaWhatsAppReplyMapper.Map(waId, body),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InboundWhatsAppReplyTimeoutException
                or NotificationProviderTransientException
                or HttpRequestException)
        {
            await FailReplyAsync(inboundMessageId, "reply_ambiguous").ConfigureAwait(false);
            await FinalizeAsync(
                inboundMessageId,
                "ambiguous",
                "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (send.Kind == NotificationProviderResultKind.Delivered
            && !string.IsNullOrWhiteSpace(send.MessageId))
        {
            var completed = await ExecuteBooleanFunctionAsync(
                "SELECT notifications.complete_inbound_whatsapp_message(@inbound_id, @reply_message_id)",
                CancellationToken.None,
                ("inbound_id", inboundMessageId),
                ("reply_message_id", send.MessageId)).ConfigureAwait(false);
            await FinalizeAsync(
                inboundMessageId,
                completed ? "completed" : "ambiguous",
                completed ? null : "agent_ambiguous",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var reason = send.Kind == NotificationProviderResultKind.Rejected
            ? "reply_rejected"
            : "reply_ambiguous";
        await FailReplyAsync(inboundMessageId, reason).ConfigureAwait(false);
        await FinalizeAsync(
            inboundMessageId,
            reason == "reply_rejected" ? "failed" : "ambiguous",
            reason == "reply_rejected" ? "agent_failed" : "agent_ambiguous",
            CancellationToken.None).ConfigureAwait(false);
    }

    private Task FailReplyAsync(Guid inboundMessageId, string reason) =>
        ExecuteBooleanFunctionAsync(
            "SELECT notifications.fail_inbound_whatsapp_message(@inbound_id, @failure_reason)",
            CancellationToken.None,
            ("inbound_id", inboundMessageId),
            ("failure_reason", reason));

    private async Task<WhatsAppClaim> ClaimWhatsAppAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status, claimed, message_type, wa_id, media_id, caption, user_text, "
            + "resolved_user_id, resolved_company_id, resolved_membership_id, agent_file_id "
            + "FROM notifications.claim_inbound_whatsapp_agent(@inbound_id)";
        AddParameter(command, "inbound_id", inboundMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InboundWhatsAppPermanentException("Inbound agent claim returned no row.");
        }

        var status = reader.GetString(0);
        var claimed = reader.GetBoolean(1);
        if (!claimed)
        {
            return WhatsAppClaim.NotClaimed(status);
        }

        return new WhatsAppClaim(
            status,
            true,
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetGuid(7),
            reader.GetGuid(8),
            reader.GetGuid(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10));
    }

    private async Task<TelegramClaim> ClaimTelegramAsync(
        Guid inboundMessageId,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status, claimed, message_type, media_id, media_mime_type, text_body, "
            + "resolved_user_id, resolved_company_id, resolved_membership_id, agent_file_id "
            + "FROM notifications.claim_inbound_telegram_agent(@inbound_id)";
        AddParameter(command, "inbound_id", inboundMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InboundWhatsAppPermanentException("Inbound Telegram agent claim returned no row.");
        }

        var status = reader.GetString(0);
        if (!reader.GetBoolean(1))
        {
            return TelegramClaim.NotClaimed(status);
        }

        return new TelegramClaim(
            status,
            true,
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetGuid(6),
            reader.GetGuid(7),
            reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9));
    }

    private Task<bool> ReleaseTelegramAsync(Guid inboundMessageId, CancellationToken cancellationToken) =>
        ExecuteBooleanFunctionAsync(
            "SELECT notifications.release_inbound_telegram_agent(@inbound_id)",
            cancellationToken,
            ("inbound_id", inboundMessageId));

    private async Task FinalizeTelegramAsync(
        Guid inboundMessageId,
        string result,
        string? reason,
        CancellationToken cancellationToken)
    {
        await ExecuteBooleanFunctionAsync(
            "SELECT notifications.finalize_inbound_telegram_agent(@inbound_id, @result, @reason)",
            cancellationToken,
            ("inbound_id", inboundMessageId),
            ("result", result),
            ("reason", reason is null ? DBNull.Value : reason)).ConfigureAwait(false);
    }

    private async Task EnqueueTelegramReplyAsync(
        Guid inboundMessageId,
        string resultKind,
        string text,
        string? deepLinkSelector,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT delivery_id, created FROM notifications.enqueue_inbound_telegram_agent_reply("
            + "@inbound_id, @result_kind, @reply_text, @deep_link_selector)";
        AddParameter(command, "inbound_id", inboundMessageId);
        AddParameter(command, "result_kind", resultKind);
        AddParameter(command, "reply_text", text);
        AddParameter(
            command,
            "deep_link_selector",
            deepLinkSelector is null ? DBNull.Value : deepLinkSelector);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0))
        {
            throw new InvalidOperationException("Inbound Telegram Agent outbox returned no delivery.");
        }
    }

    private static string ComposeTelegramMessage(TelegramClaim claim)
    {
        if (!string.IsNullOrWhiteSpace(claim.TextBody))
        {
            return claim.TextBody.Trim();
        }

        return claim.MessageType == "document"
            ? "Prepare o documento enviado para revisão no Pagaí."
            : "Analise o anexo enviado.";
    }

    private async Task FinalizeAsync(
        Guid inboundMessageId,
        string result,
        string? reason,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT notifications.finalize_inbound_whatsapp_agent(@inbound_id, @result, @reason)";
        AddParameter(command, "inbound_id", inboundMessageId);
        AddParameter(command, "result", result);
        AddParameter(command, "reason", reason is null ? DBNull.Value : reason);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> ReleaseAsync(Guid inboundMessageId, CancellationToken cancellationToken) =>
        ExecuteBooleanFunctionAsync(
            "SELECT notifications.release_inbound_whatsapp_agent(@inbound_id)",
            cancellationToken,
            ("inbound_id", inboundMessageId));

    private async Task<bool> ExecuteBooleanFunctionAsync(
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var connection = database.Database.GetDbConnection();
        if (!connection.State.HasFlag(ConnectionState.Open))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string ComposeMessage(WhatsAppClaim claim)
    {
        var parts = new[] { claim.UserText, claim.Caption }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Join("\n\n", parts);
        }

        return claim.MessageType == "document"
            ? "Prepare o documento enviado para revisão no Pagaí."
            : "Analise o anexo enviado.";
    }

    private static string ToAgentCommandChannel(AgentChannelKind channel) => channel switch
    {
        AgentChannelKind.WhatsApp => "whatsapp",
        AgentChannelKind.Telegram => "telegram",
        AgentChannelKind.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record WhatsAppClaim(
        string Status,
        bool Claimed,
        string MessageType,
        string WaId,
        string? MediaId,
        string? Caption,
        string? UserText,
        Guid UserId,
        Guid CompanyId,
        Guid MembershipId,
        Guid? AgentFileId)
    {
        public bool IsTerminalRecovery => Status == "terminal_recovery";

        public static WhatsAppClaim NotClaimed(string status) =>
            new(status, false, string.Empty, string.Empty, null, null, null, Guid.Empty, Guid.Empty, Guid.Empty, null);
    }

    private sealed record TelegramClaim(
        string Status,
        bool Claimed,
        string MessageType,
        string? MediaId,
        string? MediaType,
        string? TextBody,
        Guid UserId,
        Guid CompanyId,
        Guid MembershipId,
        Guid? AgentFileId)
    {
        public bool IsTerminalRecovery => Status == "terminal_recovery";

        public static TelegramClaim NotClaimed(string status) =>
            new(status, false, string.Empty, null, null, null, Guid.Empty, Guid.Empty, Guid.Empty, null);
    }
}
