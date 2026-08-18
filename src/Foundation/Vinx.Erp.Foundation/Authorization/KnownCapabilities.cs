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
}
