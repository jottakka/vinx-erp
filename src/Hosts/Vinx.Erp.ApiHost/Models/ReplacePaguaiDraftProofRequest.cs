using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vinx.Erp.ApiHost.Validation;
using Vinx.Erp.Paguai;

namespace Vinx.Erp.ApiHost.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReplacePaguaiDraftProofRequest
{
    [Required]
    [NonEmptyGuid]
    public Guid CompanyId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int ExpectedVersion { get; init; }

    [Required]
    [NonEmptyGuid]
    public Guid ProofFileId { get; init; }

    public ReplaceDraftProofCommand ToCommand(Guid draftId) =>
        new(CompanyId, draftId, ProofFileId, ExpectedVersion);
}
