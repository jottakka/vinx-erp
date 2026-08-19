using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vinx.Erp.AI;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.ApiHost.Agent;

/// <summary>
/// Resultado da execução na fronteira da aplicação: resposta tipada com o
/// status HTTP correspondente, ou envelope de erro {"code"} para as falhas 5xx
/// que não cabem no contrato tipado (padrão DocumentUploadResult). O replay
/// devolve exatamente a mesma resposta e o mesmo status.
/// </summary>
public sealed record AgentRunResult(AgentRunHttpResponse? Response, string? ErrorCode, int? HttpStatusCode)
{
    public bool IsSuccess => ErrorCode is null;

    public static AgentRunResult Success(AgentRunHttpResponse response, int httpStatusCode) =>
        new(response, null, httpStatusCode);

    public static AgentRunResult Failure(string errorCode, int httpStatusCode) =>
        new(null, errorCode, httpStatusCode);
}

/// <summary>
/// Serviço de aplicação de POST /agent/runs: idempotência por requestId
/// (commandName agent.run), auditoria Ai e orquestração do runner — o perfil
/// com a seleção de provider vem do catálogo composto na DI (Task 4). O texto
/// do replay é somente a resposta segura serializada — nunca prompt, resposta
/// bruta ou PII. Desfechos 5xx não são persistidos nem auditados: o mesmo
/// requestId pode retentar quando o provider volta.
/// </summary>
public sealed class AgentExecutionApplicationService(
    FoundationDbContext database,
    AuditAppender auditAppender,
    AgentRunner runner,
    ILogger<AgentExecutionApplicationService> logger)
{
    private const string CommandName = "agent.run";
    private const string ResourceType = "agent_run";
    private const string IdempotencyConflictReason = "idempotency_conflict";
    private const string IntegrityConflictReason = "integrity_conflict";
    private const string AgentUnavailableReason = "agent_unavailable";
    private const string AuditUnavailableReason = "audit_unavailable";
    private const string AgentTimeoutReason = "agent_timeout";

    /// <summary>
    /// Executa uma chamada com idempotência: replay devolve o mesmo corpo sem
    /// nova chamada ao runner; hash divergente com o mesmo requestId é 409
    /// auditado. Cancelamento externo propaga; falha transitória de banco vira
    /// 503 agent_unavailable com log seguro.
    /// </summary>
    public async Task<AgentRunResult> RunAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        CancellationToken cancellationToken)
    {
        var payloadHash = ComputePayloadHash(request);
        try
        {
            var replayed = await TryReplayAsync(actor, request, payloadHash, cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }

            var outcome = await runner.RunAsync(
                actor, request.Journey, request.Message, request.CompanyId, cancellationToken);
            var result = MapOutcome(outcome);

            // 5xx (provider/contexto/timeout) não é durável: sem registro e sem
            // auditoria, o mesmo requestId pode retentar quando o provider volta.
            return result.Response is null
                ? result
                : await PersistAsync(actor, request, payloadHash, result, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Corrida de insert concorrente com o mesmo requestId: o índice único
            // elege o vencedor e o perdedor relez o registro (padrão upload).
            database.ChangeTracker.Clear();
            return await ReturnConcurrentReplayAsync(actor, request, payloadHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            database.ChangeTracker.Clear();
            LogUnexpectedFailure(exception, AgentUnavailableReason);
            return AgentRunResult.Failure(AgentUnavailableReason, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Mapeamento puro desfecho do runner → resposta tipada + status HTTP. O
    /// texto viaja somente o redigido; requires_confirmation não expõe texto
    /// nem a ação proposta (opaca em v1); fontes estruturadas não existem em v1
    /// e seguem nulas. 5xx viram envelope de erro; timeout expõe o código
    /// estável agent_timeout, não o reason interno do modelo.
    /// </summary>
    public static AgentRunResult MapOutcome(AgentOutcome outcome) =>
        outcome.Status switch
        {
            AgentOutcomeStatus.Completed => Typed(AgentRunStatus.Result, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.NeedsClarification => Typed(
                AgentRunStatus.NeedsClarification, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.Denied => Typed(AgentRunStatus.Denied, outcome, StatusCodes.Status200OK),
            AgentOutcomeStatus.RequiresConfirmation => AgentRunResult.Success(
                new AgentRunHttpResponse(
                    AgentRunStatus.RequiresConfirmation,
                    outcome.ReasonCode,
                    Text: null,
                    Sources: null,
                    outcome.CorrelationId),
                StatusCodes.Status200OK),
            AgentOutcomeStatus.Invalid => Typed(
                AgentRunStatus.ValidationError, outcome, StatusCodes.Status400BadRequest),
            AgentOutcomeStatus.Unavailable => AgentRunResult.Failure(
                outcome.ReasonCode, StatusCodes.Status503ServiceUnavailable),
            AgentOutcomeStatus.TimedOut => AgentRunResult.Failure(
                AgentTimeoutReason, StatusCodes.Status504GatewayTimeout),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome.Status, "Unsupported agent outcome status."),
        };

    private static AgentRunResult Typed(string status, AgentOutcome outcome, int httpStatusCode) =>
        AgentRunResult.Success(
            new AgentRunHttpResponse(status, outcome.ReasonCode, outcome.Text, Sources: null, outcome.CorrelationId),
            httpStatusCode);

    /// <summary>
    /// Consulta o registro de idempotência em transação curta (o runner não
    /// roda dentro de transação aberta): replay com mesmo hash devolve o corpo
    /// armazenado sem nova chamada; hash divergente é 409 auditado.
    /// </summary>
    private async Task<AgentRunResult?> TryReplayAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        if (request.CompanyId is not { } companyId)
        {
            // Ruling (escopo do registro): a gravação exige a empresa do request
            // (FK company_id → companies e lookup company-scoped); Guid.Empty é
            // impossível. Sem empresa o runner devolve deterministicamente
            // needs_clarification/company_required antes de qualquer chamada ao
            // provider, então o replay natural já devolve o mesmo resultado.
            return null;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ApplyContextAsync(actor, companyId, cancellationToken);
        var existing = await FindIdempotencyAsync(actor.UserId, companyId, CommandName, request.RequestId, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        database.ChangeTracker.Clear();
        if (existing is null)
        {
            return null;
        }

        return string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
            ? DeserializeRunResult(existing.ResultJson)
            : await ReturnAuditedDenialAsync(
                actor, request, AgentRunStatus.Conflict, IdempotencyConflictReason, StatusCodes.Status409Conflict, cancellationToken);
    }

    /// <summary>
    /// Persiste idempotência e auditoria na MESMA transação (padrão upload):
    /// ResultJson é a resposta segura serializada. Sem empresa no request não
    /// há registro possível — somente a auditoria é gravada.
    /// </summary>
    private async Task<AgentRunResult> PersistAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        string payloadHash,
        AgentRunResult result,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        if (request.CompanyId is { } companyId)
        {
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var existing = await FindIdempotencyAsync(actor.UserId, companyId, CommandName, request.RequestId, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                database.ChangeTracker.Clear();
                return string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
                    ? DeserializeRunResult(existing.ResultJson)
                    : await ReturnAuditedDenialAsync(
                        actor, request, AgentRunStatus.Conflict, IdempotencyConflictReason, StatusCodes.Status409Conflict, cancellationToken);
            }

            database.IdempotencyRecords.Add(new IdempotencyRecord(
                Guid.NewGuid(), companyId, actor.UserId, CommandName, request.RequestId,
                payloadHash, JsonSerializer.Serialize(result)));
        }

        var response = result.Response!;
        await AppendAuditAsync(
            actor, request, response.Status, response.ReasonCode, ToAuditOutcome(response.Status), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Negação auditada em transação própria (padrão ReturnAuditedDenialAsync):
    /// usada por conflitos de idempotência que não gravam registro novo.
    /// </summary>
    private async Task<AgentRunResult> ReturnAuditedDenialAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        string status,
        string reasonCode,
        int httpStatusCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, request.CompanyId ?? Guid.Empty, cancellationToken);
            await AppendAuditAsync(actor, request, status, reasonCode, AuditOutcome.Denied, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AgentRunResult.Success(
                new AgentRunHttpResponse(status, reasonCode, Text: null, Sources: null, actor.CorrelationId),
                httpStatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, AuditUnavailableReason);
            return AgentRunResult.Failure(AuditUnavailableReason, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Corrida perdida no insert: relez o registro vencedor ou nega com 409.</summary>
    private async Task<AgentRunResult> ReturnConcurrentReplayAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        if (request.CompanyId is { } companyId)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyContextAsync(actor, companyId, cancellationToken);
            var existing = await FindIdempotencyAsync(actor.UserId, companyId, CommandName, request.RequestId, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            if (existing is not null)
            {
                return string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)
                    ? DeserializeRunResult(existing.ResultJson)
                    : await ReturnAuditedDenialAsync(
                        actor, request, AgentRunStatus.Conflict, IdempotencyConflictReason, StatusCodes.Status409Conflict, cancellationToken);
            }
        }

        return await ReturnAuditedDenialAsync(
            actor, request, AgentRunStatus.Conflict, IntegrityConflictReason, StatusCodes.Status409Conflict, cancellationToken);
    }

    private static AgentRunResult DeserializeRunResult(string resultJson) =>
        JsonSerializer.Deserialize<AgentRunResult>(resultJson) is { Response: not null } replay
            ? replay
            : AgentRunResult.Failure(AgentUnavailableReason, StatusCodes.Status503ServiceUnavailable);

    private Task AppendAuditAsync(
        ActorContext actor,
        AgentRunHttpRequest request,
        string status,
        string? reasonCode,
        AuditOutcome outcome,
        CancellationToken cancellationToken) =>
        auditAppender.AppendAsync(new AuditEventData(
            actor.UserId,
            request.CompanyId is null ? AuditScope.Global : AuditScope.Company,
            request.CompanyId,
            AuditSource.Ai,
            CommandName,
            ResourceType,
            request.RequestId,
            outcome,
            new CorrelationContext(actor.CorrelationId).CorrelationId,
            reasonCode,
            JsonSerializer.Serialize(new AgentRunAuditMetadata(status, reasonCode))), cancellationToken);

    // Negação (política/capacidade) e validação rejeitada são denied no padrão
    // do repo; os demais desfechos duráveis são sucesso da interação.
    private static AuditOutcome ToAuditOutcome(string status) =>
        status is AgentRunStatus.Denied or AgentRunStatus.ValidationError
            ? AuditOutcome.Denied
            : AuditOutcome.Success;

    private void LogUnexpectedFailure(Exception exception, string errorCode) =>
        logger.LogError(exception, "Agent run persistence failed with code {ErrorCode}", errorCode);

    private Task<IdempotencyRecord?> FindIdempotencyAsync(
        Guid actorUserId,
        Guid companyId,
        string commandName,
        Guid requestId,
        CancellationToken cancellationToken) =>
        database.IdempotencyRecords.SingleOrDefaultAsync(record =>
            record.CompanyId == companyId
            && record.ActorUserId == actorUserId
            && record.CommandName == commandName
            && record.RequestId == requestId,
            cancellationToken);

    private async Task ApplyContextAsync(ActorContext actor, Guid companyId, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)", cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)", cancellationToken);
    }

    // Hash do payload normalizado (jornada/mensagem com trim + empresa); o
    // registro persiste somente o hash, nunca a mensagem.
    private static string ComputePayloadHash(AgentRunHttpRequest request) =>
        ComputeHash(JsonSerializer.Serialize(new AgentRunIdempotencyPayload(
            request.Journey.Trim(),
            request.Message.Trim(),
            request.CompanyId)));

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record AgentRunIdempotencyPayload(string Journey, string Message, Guid? CompanyId);
    private sealed record AgentRunAuditMetadata(string Status, string? ReasonCode);
}
