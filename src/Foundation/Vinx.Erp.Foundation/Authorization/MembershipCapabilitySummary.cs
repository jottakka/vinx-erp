namespace Vinx.Erp.Foundation.Authorization;

public sealed record MembershipCapabilitySummary(
    Guid MembershipId,
    Guid CompanyId,
    string CompanyName,
    IReadOnlyList<EffectiveCapability> Capabilities
);
