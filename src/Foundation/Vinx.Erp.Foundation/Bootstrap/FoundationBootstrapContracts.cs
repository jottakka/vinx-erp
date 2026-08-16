namespace Vinx.Erp.Foundation.Bootstrap;

public sealed record FoundationBootstrapResponse(
    FoundationBootstrapUser User,
    IReadOnlyList<FoundationBootstrapCompany> Companies,
    IReadOnlyDictionary<string, bool> Flags);

public sealed record FoundationBootstrapUser(Guid Id, string DisplayName);

public sealed record FoundationBootstrapCompany(
    Guid MembershipId,
    Guid CompanyId,
    string CompanyName,
    IReadOnlyList<FoundationBootstrapCapability> Capabilities);

public sealed record FoundationBootstrapCapability(
    string Name,
    IReadOnlyList<string> Scopes);
