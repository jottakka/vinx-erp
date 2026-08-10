import type {
  AuthorizedCompanyMembership,
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
  user: DisplayUser;
};

export const mockCompanyFixtures = {
  alfa: {
    access: "authorized",
    capabilities: ["prototype.view"],
    id: "alfa",
    name: "Alfa",
  },
  beta: {
    access: "authorized",
    capabilities: ["prototype.view"],
    id: "beta",
    name: "Beta",
  },
  gama: {
    access: "denied",
    capabilities: [],
    id: "gama",
    name: "Gama",
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
    flags: {
      prototypeSession: true,
    },
    user: {
      id: "maria",
      name: "Maria",
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
    user: profile.user,
  };
}
