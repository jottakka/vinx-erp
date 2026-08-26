using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;

/// <summary>
/// Costura de parametrização dos serviços públicos de aprovação. O legado do
/// módulo AI permanece intacto via <see cref="Legacy"/>; módulos consumidores
/// novos (ex.: demandas) declaram prefixo de comando e capability próprios.
/// A capability é SEMPRE exigida em escopo company — o seam não afrouxa a
/// autorização, apenas nomeia quem confirma/veta.
/// </summary>
namespace Vinx.Erp.Foundation.Approvals;

public sealed record ApprovalCapabilityPolicy(
    string PrepareCommandName,
    string ExpireCommandName,
    string ConfirmCommandName,
    string VetoCommandName,
    string CapabilityName,
    AuditSource AuditSource = AuditSource.Ai)
{
    public static readonly ApprovalCapabilityPolicy Legacy = new(
        PrepareCommandName: "ai.approvals.prepare",
        ExpireCommandName: "ai.approvals.expire",
        ConfirmCommandName: "ai.approvals.confirm",
        VetoCommandName: "ai.approvals.veto",
        CapabilityName: KnownCapabilities.AiWritesConfirm
    );

    /// <summary>Política por módulo com pino de privilege atual e fonte de
    /// auditoria própria.</summary>
    public static ApprovalCapabilityPolicy ForModule(string modulePrefix, string capability, AuditSource auditSource) =>
        new(
            PrepareCommandName: $"{modulePrefix}.approvals.prepare",
            ExpireCommandName: $"{modulePrefix}.approvals.expire",
            ConfirmCommandName: $"{modulePrefix}.approvals.confirm",
            VetoCommandName: $"{modulePrefix}.approvals.veto",
            CapabilityName: capability,
            AuditSource: auditSource
        );

    /// <summary>Só altera a capability; nomes de comando ficam no legado AI.</summary>
    public static ApprovalCapabilityPolicy FromCapability(string capability) =>
        Legacy with { CapabilityName = capability };
}
