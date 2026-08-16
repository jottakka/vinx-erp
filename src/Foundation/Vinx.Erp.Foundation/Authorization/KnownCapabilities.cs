namespace Vinx.Erp.Foundation.Authorization;

public static class KnownCapabilities
{
    public const string RolesAssign = "foundation.roles.assign";
    public static readonly Guid RolesAssignId = Guid.Parse("00341b57-025e-5a71-a205-f0a6854a991f");

    public const string FilesUpload = "foundation.files.upload";
    public static readonly Guid FilesUploadId = Guid.Parse("e09997ad-8dda-58f6-8e9f-568bf84c9ab3");

    public const string FilesConfirm = "foundation.files.confirm";
    public static readonly Guid FilesConfirmId = Guid.Parse("4687cfe7-2318-58ca-8ac9-fff0dad042b0");

    public const string MembershipsRevoke = "foundation.memberships.revoke";
    public static readonly Guid MembershipsRevokeId = Guid.Parse(
        "46285f1b-bda6-5fba-b0e9-74515120b076"
    );

    // Primeira capability cross-module (prefixo ai.*): confirmação de escritas
    // de ferramentas do harness. UUID canônico derivado por nome (UUIDv5 de
    // https://vinx.erp/capabilities/ai.writes.confirm).
    public const string AiWritesConfirm = "ai.writes.confirm";
    public static readonly Guid AiWritesConfirmId = Guid.Parse(
        "9e77f769-2093-55a5-a482-85ed3e83dc88"
    );

    public const string PaguaiDraftRead = "paguai.draft.read";
    public static readonly Guid PaguaiDraftReadId = Guid.Parse(
        "766bdb56-80c0-5dee-982f-be7f0d3eaea8"
    );

    public const string PaguaiDraftCreate = "paguai.draft.create";
    public static readonly Guid PaguaiDraftCreateId = Guid.Parse(
        "aecaa296-6a6f-5815-93ac-bd1ccdff3f44"
    );

    public const string ServiceDeskTicketReadQueue = "service_desk.ticket.read_queue";
    public static readonly Guid ServiceDeskTicketReadQueueId = Guid.Parse(
        "611e4143-0a7a-49df-97af-783b28dd8974"
    );

    public const string ServiceDeskTicketCreate = "service_desk.ticket.create";
    public static readonly Guid ServiceDeskTicketCreateId = Guid.Parse(
        "20d15110-0862-4098-96f6-0a1931d48848"
    );

    public const string ServiceDeskTicketAssign = "service_desk.ticket.assign";
    public static readonly Guid ServiceDeskTicketAssignId = Guid.Parse(
        "7a4911f9-5ff2-4cad-b8e4-f907ff3159b3"
    );

    public const string ServiceDeskTicketMove = "service_desk.ticket.move";
    public static readonly Guid ServiceDeskTicketMoveId = Guid.Parse(
        "86622ee5-7117-471a-90a7-1ecd90fabc18"
    );
}
