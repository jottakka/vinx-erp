using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

/// <summary>Corpo do comando de campos de ticket; ausente significa inalterado.</summary>
public sealed class UpdateAtendimentoCardFieldsRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    /// <summary>critica | alta | media | baixa; null mantém o valor atual.</summary>
    public string? Priority { get; init; }

    public Guid? CategoryRefId { get; init; }

    public Guid? ChannelRefId { get; init; }

    /// <summary>ISO-8601; data no passado é rejeitada com 400.</summary>
    public DateTimeOffset? SlaDueAt { get; init; }

    public string? RequesterPersonRef { get; init; }

    /// <summary>Menções disparam intents determinísticas pós-commit.</summary>
    public IReadOnlyList<Guid>? MentionedMembershipIds { get; init; }

    public UpdateAtendimentoCardFieldsCommand ToCommand() =>
        new(
            CompanyId,
            RequestId,
            ExpectedVersion,
            Priority?.Trim(),
            CategoryRefId,
            ChannelRefId,
            SlaDueAt,
            RequesterPersonRef,
            MentionedMembershipIds
        );
}
