namespace Vinx.Erp.ApiHost.Models;

/// <summary>Resposta opaca do convite de membro (o schema OpenAPI é inferido deste tipo).</summary>
internal sealed record MemberInvitationCreatedResponse(
    Guid MembershipId,
    Guid InvitationId,
    string InvitationStatus
);
