import type {
  AuthorizedCompanyMembership,
  BootstrapPlatform,
  BootstrapSnapshot,
  DisplayUser,
  EffectiveFlags,
} from "./session-contract";

export const unavailableDevelopmentProfileMessage =
  "Perfil de desenvolvimento indisponível";

type MockCompanyFixture = AuthorizedCompanyMembership & {
  access: "authorized" | "denied";
};

type MockSessionProfileFixture = {
  companies: readonly MockCompanyFixture[];
  flags: EffectiveFlags;
  platform: BootstrapPlatform;
  user: DisplayUser;
};

export const mockCompanyFixtures = {
  alfa: {
    access: "authorized",
    capabilities: [{ name: "prototype.view", scopes: ["Company"] }],
    companyId: "33333333-3333-4333-8333-333333333333",
    companyName: "Alfa",
    membershipId: "22222222-2222-4222-8222-222222222222",
  },
  beta: {
    access: "authorized",
    capabilities: [{ name: "prototype.view", scopes: ["Company"] }],
    companyId: "55555555-5555-4555-8555-555555555555",
    companyName: "Beta",
    membershipId: "44444444-4444-4444-8444-444444444444",
  },
  gama: {
    access: "denied",
    capabilities: [],
    companyId: "77777777-7777-4777-8777-777777777777",
    companyName: "Gama",
    membershipId: "66666666-6666-4666-8666-666666666666",
  },
} as const satisfies Record<string, MockCompanyFixture>;

export const mockSessionProfiles: Readonly<
  Record<string, MockSessionProfileFixture>
> = {
  maria: {
    companies: [
      mockCompanyFixtures.alfa,
      mockCompanyFixtures.beta,
      mockCompanyFixtures.gama,
    ],
    flags: {},
    platform: { isPlatformOperator: false },
    user: {
      id: "maria",
      displayName: "Maria",
    },
  },
};

export function createBootstrapSnapshot(
  profile: MockSessionProfileFixture,
): BootstrapSnapshot {
  return {
    companies: profile.companies
      .filter((company) => company.access === "authorized")
      .map(({ access: _access, ...company }) => company),
    flags: profile.flags,
    platform: profile.platform,
    user: profile.user,
  };
}
