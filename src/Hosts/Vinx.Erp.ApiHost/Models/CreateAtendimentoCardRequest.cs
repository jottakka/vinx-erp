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

    [NonEmptyGuid]
    public Guid? AttachmentFileId { get; init; }

    public IReadOnlyList<Guid>? ParticipantMembershipIds { get; init; }

    public CreateAtendimentoCardCommand ToCommand() =>
        new(CompanyId, RequestId, Title, Description)
        {
            AttachmentFileId = AttachmentFileId,
            ParticipantMembershipIds = ParticipantMembershipIds ?? [],
        };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
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
