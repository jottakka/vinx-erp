export type CapabilityScope = "Self" | "Team" | "Unit" | "Company" | "Group";

export type EffectiveCapability = {
  name: string;
  scopes: readonly CapabilityScope[];
};

export type DisplayUser = {
  id: string;
  displayName: string;
};

export type AuthorizedCompanyMembership = {
  membershipId: string;
  companyId: string;
  companyName: string;
  capabilities: readonly EffectiveCapability[];
};

export type EffectiveFlags = Readonly<Record<string, boolean>>;

export type BootstrapPlatform = {
  isPlatformOperator: boolean;
};

/**
 * Projeção de UX autorizada pelo servidor. Ela permanece em memória e não
 * substitui a autorização da operação no backend.
 */
export type BootstrapSnapshot = {
  user: DisplayUser;
  companies: readonly AuthorizedCompanyMembership[];
  flags: EffectiveFlags;
  platform: BootstrapPlatform;
};

export type MockSessionGateway = {
  login(profileId: string): Promise<BootstrapSnapshot>;
  logout(): Promise<void>;
  getBootstrap(): Promise<BootstrapSnapshot | null>;
};
