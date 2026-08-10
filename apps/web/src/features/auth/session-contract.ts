export type DisplayUser = {
  id: string;
  name: string;
};

export type AuthorizedCompanyMembership = {
  id: string;
  name: string;
  capabilities: readonly string[];
};

export type EffectiveFlags = {
  prototypeSession: boolean;
};

export type BootstrapSnapshot = {
  user: DisplayUser;
  companies: readonly AuthorizedCompanyMembership[];
  flags: EffectiveFlags;
};

export type MockSessionGateway = {
  login(profileId: string): Promise<BootstrapSnapshot>;
  logout(): Promise<void>;
  getBootstrap(): Promise<BootstrapSnapshot | null>;
};
