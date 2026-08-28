namespace Vinx.Erp.Foundation.Authorization;

public static class KnownCapabilities
{
    public const string RolesAssign = "foundation.roles.assign";
    public static readonly Guid RolesAssignId = Guid.Parse("00341b57-025e-5a71-a205-f0a6854a991f");

    public const string FilesUpload = "foundation.files.upload";
    public static readonly Guid FilesUploadId = Guid.Parse("e09997ad-8dda-58f6-8e9f-568bf84c9ab3");

    public const string FilesConfirm = "foundation.files.confirm";
    public static readonly Guid FilesConfirmId = Guid.Parse("4687cfe7-2318-58ca-8ac9-fff0dad042b0");

    // Leitura autorizada de documentos para contexto do agente (#134-A):
    // menor privilégio — nunca reutiliza upload/confirm. UUID canônico por
    // nome (UUIDv5 de https://vinx.erp/capabilities/foundation.files.read-ocr).
    public const string FilesReadOcr = "foundation.files.read-ocr";
    public static readonly Guid FilesReadOcrId = Guid.Parse(
        "f9df3888-cadb-5344-bc68-3555b99dae86"
    );

    public const string MembershipsRevoke = "foundation.memberships.revoke";
    public static readonly Guid MembershipsRevokeId = Guid.Parse(
        "46285f1b-bda6-5fba-b0e9-74515120b076"
    );

    public const string EmployeesCreate = "foundation.employees.create";
    public static readonly Guid EmployeesCreateId = Guid.Parse(
        "b81d5f86-6f68-5846-bbe7-61a244f9e212"
    );

    // Primeira capability cross-module (prefixo ai.*): confirmação de escritas
    // de ferramentas do harness. UUID canônico derivado por nome (UUIDv5 de
    // https://vinx.erp/capabilities/ai.writes.confirm).
    public const string AiWritesConfirm = "ai.writes.confirm";
    public static readonly Guid AiWritesConfirmId = Guid.Parse(
        "9e77f769-2093-55a5-a482-85ed3e83dc88"
    );

    public const string AiRead = "ai.read";
    public static readonly Guid AiReadId = Guid.Parse(
        "46db8e9e-5bd5-59ea-a371-0312ba8fafb8"
    );

    public const string PaguaiDraftRead = "paguai.draft.read";
    public static readonly Guid PaguaiDraftReadId = Guid.Parse(
        "766bdb56-80c0-5dee-982f-be7f0d3eaea8"
    );

    public const string PaguaiDraftCreate = "paguai.draft.create";
    public static readonly Guid PaguaiDraftCreateId = Guid.Parse(
        "aecaa296-6a6f-5815-93ac-bd1ccdff3f44"
    );

    public const string PaguaiDraftApprove = "paguai.draft.approve";
    public static readonly Guid PaguaiDraftApproveId = Guid.Parse(
        "e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a"
    );

    public const string ServiceDeskTicketReadQueue = "service_desk.ticket.read_queue";
    public static readonly Guid ServiceDeskTicketReadQueueId = Guid.Parse(
        "f02fa2b2-aa34-50ca-99bd-b17a55d2989f"
    );

    public const string ServiceDeskTicketCreate = "service_desk.ticket.create";
    public static readonly Guid ServiceDeskTicketCreateId = Guid.Parse(
        "e7a53081-b6d9-5509-9300-bff9204eaa8f"
    );

    public const string ServiceDeskTicketAssign = "service_desk.ticket.assign";
    public static readonly Guid ServiceDeskTicketAssignId = Guid.Parse(
        "e5f8c74a-d854-57bb-916f-25b63824cab7"
    );

    public const string ServiceDeskTicketMove = "service_desk.ticket.move";
    public static readonly Guid ServiceDeskTicketMoveId = Guid.Parse(
        "db9f6a71-6da5-5933-8204-6b16cad2af8a"
    );

    // Fatia de usabilidade P6: campos de ticket (prioridade/categoria/canal/
    // SLA/solicitante). UUID canônico UUIDv5 por nome, mesmo padrão acima.
    public const string ServiceDeskTicketFields = "service_desk.ticket.fields";
    public static readonly Guid ServiceDeskTicketFieldsId = Guid.Parse(
        "0e772f6e-97e9-5e8b-82ba-f254691b5d22"
    );

    public const string ServiceDeskTicketWatchers = "service_desk.ticket.watchers";
    public static readonly Guid ServiceDeskTicketWatchersId = Guid.Parse(
        "3886e350-3784-5f75-9289-8cb5b0a138f6"
    );

    public const string ServiceDeskLookupAdmin = "service_desk.lookup.admin";
    public static readonly Guid ServiceDeskLookupAdminId = Guid.Parse(
        "84899de4-b2ff-56bd-8720-264e61d11148"
    );

    /// <summary>
    /// Leitura de diretório para pickers (menor privilégio): deliberadamente
    /// distinta de EmployeesCreate, que autoriza criação de funcionário.
    /// </summary>
    public const string DirectoryRead = "directory.read";
    public static readonly Guid DirectoryReadId = Guid.Parse(
        "128d103b-1449-5962-98ac-1a7d8edd1063"
    );

    // Cancelamento é terminal e irreversível, com ação de auditoria própria:
    // capability dedicada em vez de reutilizar move (menor privilégio). UUID
    // canônico por nome (UUIDv5 de https://vinx.erp/capabilities/service_desk.ticket.cancel).
    public const string ServiceDeskTicketCancel = "service_desk.ticket.cancel";
    public static readonly Guid ServiceDeskTicketCancelId = Guid.Parse(
        "ffe3ae25-dab3-5aaa-acde-b56f9797ce01"
    );
}
