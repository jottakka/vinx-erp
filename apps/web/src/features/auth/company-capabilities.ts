import type { AuthorizedCompanyMembership } from "./session-contract";

export function hasCompanyCapability(
  company: Pick<AuthorizedCompanyMembership, "capabilities"> | undefined,
  capabilityName: string,
): boolean {
  return (
    company?.capabilities.some(
      (capability) =>
        capability.name === capabilityName &&
        capability.scopes.includes("Company"),
    ) ?? false
  );
}
