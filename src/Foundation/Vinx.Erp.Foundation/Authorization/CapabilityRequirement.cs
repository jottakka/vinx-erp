using Vinx.Erp.Foundation.Domain;

namespace Vinx.Erp.Foundation.Authorization;

public sealed record CapabilityRequirement(string Name, CapabilityScope RequiredScope);
