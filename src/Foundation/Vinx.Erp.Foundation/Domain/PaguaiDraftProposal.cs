namespace Vinx.Erp.Foundation.Domain;

/// <summary>Proposta Pagaí tipada e efêmera, vinculada ao ator, empresa, request e arquivo autorizados.</summary>
public sealed class PaguaiDraftProposal : ICompanyScoped, IConcurrencyVersioned
{
    public const string PendingState = "pending";
    public const string ConsumedState = "consumed";
    public const string RejectedState = "rejected";
    public const string InvalidatedState = "invalidated";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private PaguaiDraftProposal()
    {
    }

    public Guid Id { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid FileRecordId { get; private set; }
    public string Title { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateOnly DueDate { get; private set; }
    public string BeneficiaryDisplayName { get; private set; } = null!;
    public string ProofRequirement { get; private set; } = null!;
    public double Confidence { get; private set; }
    public string SourceReferences { get; private set; } = null!;
    public string PolicyVersion { get; private set; } = null!;
    public string PayloadHash { get; private set; } = null!;
    public string State { get; private set; } = null!;
    public Guid? ResultDraftId { get; private set; }
    public Guid? ResolutionRequestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public long RowVersion { get; private set; } = 1;

    /// <summary>Cria a proposta usando o instante fornecido pelo relógio do PostgreSQL.</summary>
    public static PaguaiDraftProposal Create(
        Guid id,
        Guid companyId,
        Guid actorUserId,
        Guid requestId,
        Guid fileRecordId,
        string title,
        string description,
        decimal amount,
        string currency,
        DateOnly dueDate,
        string beneficiaryDisplayName,
        string proofRequirement,
        double confidence,
        string sourceReferences,
        string policyVersion,
        string payloadHash,
        DateTimeOffset databaseNow)
    {
        ThrowIfEmpty(id, nameof(id));
        ThrowIfEmpty(companyId, nameof(companyId));
        ThrowIfEmpty(actorUserId, nameof(actorUserId));
        ThrowIfEmpty(requestId, nameof(requestId));
        ThrowIfEmpty(fileRecordId, nameof(fileRecordId));
        ValidateText(title, 200, nameof(title));
        ValidateText(description, 4_000, nameof(description));
        ValidateText(beneficiaryDisplayName, 200, nameof(beneficiaryDisplayName));
        ValidateText(sourceReferences, 2_000, nameof(sourceReferences));
        ValidateText(policyVersion, 100, nameof(policyVersion));
        if (amount <= 0 || ((decimal.GetBits(amount)[3] >> 16) & 0x7F) > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        if (currency != "BRL")
        {
            throw new ArgumentException("Currency must be BRL.", nameof(currency));
        }

        if (dueDate == default)
        {
            throw new ArgumentException("Due date is required.", nameof(dueDate));
        }

        if (proofRequirement is not ("optional" or "required"))
        {
            throw new ArgumentException("Proof requirement is invalid.", nameof(proofRequirement));
        }

        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        if (payloadHash.Length != 64 || payloadHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Payload hash must be a SHA-256 hexadecimal value.", nameof(payloadHash));
        }

        if (databaseNow == default)
        {
            throw new ArgumentException("Database timestamp is required.", nameof(databaseNow));
        }

        return new PaguaiDraftProposal
        {
            Id = id,
            CompanyId = companyId,
            ActorUserId = actorUserId,
            RequestId = requestId,
            FileRecordId = fileRecordId,
            Title = title,
            Description = description,
            Amount = amount,
            Currency = currency,
            DueDate = dueDate,
            BeneficiaryDisplayName = beneficiaryDisplayName,
            ProofRequirement = proofRequirement,
            Confidence = confidence,
            SourceReferences = sourceReferences,
            PolicyVersion = policyVersion,
            PayloadHash = payloadHash.ToLowerInvariant(),
            State = PendingState,
            CreatedAt = databaseNow,
            ExpiresAt = databaseNow.Add(Lifetime),
        };
    }

    public void Consume(Guid draftId, Guid actorUserId, Guid requestId, DateTimeOffset databaseNow)
    {
        ThrowIfEmpty(draftId, nameof(draftId));
        Resolve(ConsumedState, actorUserId, requestId, databaseNow, requireUnexpired: true);
        ResultDraftId = draftId;
    }

    public void Reject(Guid actorUserId, Guid requestId, DateTimeOffset databaseNow) =>
        Resolve(RejectedState, actorUserId, requestId, databaseNow, requireUnexpired: true);

    public void Invalidate(Guid actorUserId, Guid requestId, DateTimeOffset databaseNow) =>
        Resolve(InvalidatedState, actorUserId, requestId, databaseNow, requireUnexpired: false);

    public bool ExpireIfDue(DateTimeOffset databaseNow, Guid actorUserId, Guid requestId)
    {
        if (State != PendingState || databaseNow < ExpiresAt)
        {
            return false;
        }

        Resolve(InvalidatedState, actorUserId, requestId, databaseNow, requireUnexpired: false);
        return true;
    }

    private void Resolve(
        string targetState,
        Guid actorUserId,
        Guid requestId,
        DateTimeOffset databaseNow,
        bool requireUnexpired)
    {
        if (State != PendingState)
        {
            throw new InvalidOperationException("Proposal is no longer pending.");
        }

        ThrowIfEmpty(actorUserId, nameof(actorUserId));
        ThrowIfEmpty(requestId, nameof(requestId));
        if (databaseNow < CreatedAt || (requireUnexpired && databaseNow >= ExpiresAt))
        {
            throw new InvalidOperationException("Proposal cannot be resolved at this time.");
        }

        State = targetState;
        ResolutionRequestId = requestId;
        ResolvedAt = databaseNow;
    }

    private static void ValidateText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value != value.Trim() || value.Any(char.IsControl))
        {
            throw new ArgumentException("Value is not canonical safe text.", parameterName);
        }
    }

    private static void ThrowIfEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier is required.", parameterName);
        }
    }
}
