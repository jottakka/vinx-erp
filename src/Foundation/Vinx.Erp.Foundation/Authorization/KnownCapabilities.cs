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

    // Núcleo de Demandas (kanban operacional interno). UUIDv5 canônico por nome,
    // mesma derivação das capabilities anteriores.
    public const string DemandasDemandRead = "demandas.demand.read";
    public static readonly Guid DemandasDemandReadId = Guid.Parse(
        "6ff8b25a-1c37-58cc-894b-97221467c3d4"
    );

    public const string DemandasDemandCreate = "demandas.demand.create";
    public static readonly Guid DemandasDemandCreateId = Guid.Parse(
        "d3c10860-a26c-5eac-b684-8e2faac9ad99"
    );

    public const string DemandasDemandAdvance = "demandas.demand.advance";
    public static readonly Guid DemandasDemandAdvanceId = Guid.Parse(
        "74403a29-bdae-55a9-b43b-774eca3f03b2"
    );

    public const string DemandasDemandAssign = "demandas.demand.assign";
    public static readonly Guid DemandasDemandAssignId = Guid.Parse(
        "00b09e31-8693-5b62-b59a-cad5994f3378"
    );

    public const string DemandasParticipantsManage = "demandas.demand.participants.manage";
    public static readonly Guid DemandasParticipantsManageId = Guid.Parse(
        "d207f7a5-7fdb-5a7d-a87c-2b4210f36712"
    );

    public const string DemandasRoutineManage = "demandas.routine.manage";
    public static readonly Guid DemandasRoutineManageId = Guid.Parse(
        "ec28a347-1ade-5586-bf33-e6f1cbd59620"
    );
}
