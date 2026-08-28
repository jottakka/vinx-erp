namespace Vinx.Erp.Foundation.Domain;

/// <summary>
/// Aprovação durável de uso único para uma escrita de ferramenta do módulo AI.
/// O ciclo é preparado → confirmado (consumo) ou vetado; nenhum estado retorna
/// a pendente. A mutação de negócio associada possui idempotência e auditoria
/// próprias; a auditoria desta aprovação não a substitui.
/// </summary>
public sealed class FoundationApproval : SoftDeletableEntity, ICompanyScoped
{
    public const string VetoReason = "approval_vetoed";
    public const string ExpiredReason = "approval_expired";

    private FoundationApproval()
    {
    }

    public FoundationApproval(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string action,
        string payloadHash,
        string policyVersion,
        Guid proposalId,
        DateTimeOffset expiresAt,
        Guid requestId,
        string resourceKey = "")
    {
        ValidateCore(id, companyId, actorUserId, action, payloadHash, policyVersion, proposalId, requestId);
        ValidateResourceKey(resourceKey);

        CompanyId = companyId;
        ActorUserId = actorUserId;
        Action = action;
        PayloadHash = payloadHash;
        PolicyVersion = policyVersion;
        ProposalId = proposalId;
        ResourceKey = resourceKey;
        ExpiresAt = expiresAt;
        InitializeLifecycle(id, actorUserId, requestId);
        if (ExpiresAt <= CreatedAt)
        {
            throw new ArgumentException("The approval expiration must be after its creation.", nameof(expiresAt));
        }
    }

    public FoundationApproval(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string action,
        string payloadHash,
        string policyVersion,
        Guid proposalId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        Guid requestId,
        string resourceKey = "")
    {
        ValidateCore(id, companyId, actorUserId, action, payloadHash, policyVersion, proposalId, requestId);
        ValidateResourceKey(resourceKey);

        CompanyId = companyId;
        ActorUserId = actorUserId;
        Action = action;
        PayloadHash = payloadHash;
        PolicyVersion = policyVersion;
        ProposalId = proposalId;
        ResourceKey = resourceKey;
        ExpiresAt = expiresAt;
        InitializeLifecycle(id, actorUserId, requestId, createdAt);
        if (ExpiresAt <= CreatedAt)
        {
            throw new ArgumentException("The approval expiration must be after its creation.", nameof(expiresAt));
        }
    }

    private static void ValidateResourceKey(string resourceKey)
    {
        if (resourceKey.Length > 200)
        {
            throw new ArgumentException("The approval resource key is out of bounds.", nameof(resourceKey));
        }
    }

    private static void ValidateCore(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        string action,
        string payloadHash,
        string policyVersion,
        Guid proposalId,
        Guid requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        if (id == Guid.Empty || companyId == Guid.Empty || actorUserId == Guid.Empty ||
            proposalId == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Approval identifiers must not be empty.", nameof(id));
        }
    }

    public Guid CompanyId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string PayloadHash { get; private set; } = null!;
    public string PolicyVersion { get; private set; } = null!;
    public Guid ProposalId { get; private set; }

    /// <summary>
    /// Chave do recurso da fronteira de proposta viva (vazio = single-flight
    /// global por ator/empresa, o contrato legado do módulo AI).
    /// </summary>
    public string ResourceKey { get; private set; } = "";

    /// <summary>
    /// <c>RequestId</c> da confirmação vencedora (escopo CONFIRMATION), nulo
    /// enquanto a aprovação está viva. Distinto do <see cref="AuditableEntity.RequestId"/>
    /// herdado, que registra o preparo/última mutação.
    /// </summary>
    public Guid? ConfirmationRequestId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public string? ResultJson { get; private set; }

    public Company Company { get; private set; } = null!;
    public User ActorUser { get; private set; } = null!;

    public bool IsConsumed => ConsumedAt is not null;
    public bool IsVetoed => DeletedAt is not null && DeletedReason == VetoReason;
    public bool IsExpired => DeletedAt is not null && DeletedReason == ExpiredReason;

    /// <summary>
    /// Marca o consumo single-use com o <c>request_id</c> da confirmação
    /// vencedora e o resultado original (para replays). O caminho de produção
    /// usa o UPDATE condicional em SQL; este método espelha a transição para
    /// leituras e provas.
    /// </summary>
    public void MarkConsumed(Guid confirmationRequestId, string resultJson, Guid actorUserId)
    {
        if (confirmationRequestId == Guid.Empty)
        {
            throw new ArgumentException("The confirmation request identifier must not be empty.", nameof(confirmationRequestId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        if (IsConsumed || IsVetoed)
        {
            throw new InvalidOperationException("The approval is already in a terminal state.");
        }

        ConsumedAt = DateTimeOffset.UtcNow;
        ConfirmationRequestId = confirmationRequestId;
        ResultJson = resultJson;
        RecordUpdate(actorUserId, confirmationRequestId);
    }

    /// <summary>
    /// Veta a aprovação (estado terminal). O veto é um soft-delete com motivo
    /// fixo; nenhuma confirmação posterior executa.
    /// </summary>
    public void Veto(Guid actorUserId, Guid requestId)
    {
        if (IsConsumed)
        {
            throw new InvalidOperationException("A consumed approval cannot be vetoed.");
        }
        if (IsVetoed)
        {
            return;
        }

        DeletedAt = DateTimeOffset.UtcNow;
        DeletedByUserId = actorUserId;
        DeletedReason = VetoReason;
        RecordUpdate(actorUserId, requestId);
    }
}
