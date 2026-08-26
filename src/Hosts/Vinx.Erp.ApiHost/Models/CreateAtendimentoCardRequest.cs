using System.ComponentModel.DataAnnotations;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Atendimento;

namespace Vinx.Erp.ApiHost.Models;

public sealed class CreateAtendimentoCardRequest : IValidatableObject
{
    public const int MaxParticipants = 25;

    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid RequestId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(4000)]
    public string Description { get; init; } = string.Empty;

    public string? Priority { get; init; }

    public string? Channel { get; init; }

    public DateTimeOffset? SlaDueAt { get; init; }

    [NonEmptyGuid]
    public Guid? AttachmentFileId { get; init; }

    public IReadOnlyList<Guid>? ParticipantMembershipIds { get; init; }

    public CreateAtendimentoCardCommand ToCommand() =>
        new(CompanyId, RequestId, Title, Description)
        {
            Priority = Priority,
            Channel = Channel,
            SlaDueAt = SlaDueAt,
            AttachmentFileId = AttachmentFileId,
            ParticipantMembershipIds = ParticipantMembershipIds ?? [],
        };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Priority is not null && Priority is not (AtendimentoPriority.Critical or AtendimentoPriority.High or AtendimentoPriority.Medium or AtendimentoPriority.Low))
        {
            yield return new ValidationResult("Priority must be critica, alta, media, or baixa.", [nameof(Priority)]);
        }

        if (Channel is not null && Channel is not (AtendimentoChannel.WhatsApp or AtendimentoChannel.Email or AtendimentoChannel.Web or AtendimentoChannel.Manual))
        {
            yield return new ValidationResult("Channel must be whatsapp, email, web, or manual.", [nameof(Channel)]);
        }

        if (ParticipantMembershipIds?.Any(id => id == Guid.Empty) == true)
        {
            yield return new ValidationResult(
                "Participant membership IDs must be non-empty UUIDs.",
                [nameof(ParticipantMembershipIds)]
            );
        }

        if (ParticipantMembershipIds is { Count: > MaxParticipants })
        {
            yield return new ValidationResult(
                $"Participant membership IDs must contain at most {MaxParticipants} entries.",
                [nameof(ParticipantMembershipIds)]
            );
        }
    }
}
