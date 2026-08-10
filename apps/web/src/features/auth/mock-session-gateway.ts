import {
  createBootstrapSnapshot,
  mockSessionProfiles,
  unavailableDevelopmentProfileMessage,
} from "./mock-session-fixtures";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";

export type MockSessionGatewayOptions = {
  beforeLogin?: () => Promise<void>;
  beforeLogout?: () => Promise<void>;
};

function copyBootstrap(snapshot: BootstrapSnapshot): BootstrapSnapshot {
  return structuredClone(snapshot);
}

export function createMockSessionGateway(
  options: MockSessionGatewayOptions = {},
): MockSessionGateway {
  let currentBootstrap: BootstrapSnapshot | null = null;

  return {
    async login(profileId) {
      const profile = mockSessionProfiles[profileId];

      if (!profile) {
        throw new Error(unavailableDevelopmentProfileMessage);
      }

      await options.beforeLogin?.();
      currentBootstrap = copyBootstrap(createBootstrapSnapshot(profile));
      return copyBootstrap(currentBootstrap);
    },
    async logout() {
      await options.beforeLogout?.();
      currentBootstrap = null;
    },
    async getBootstrap() {
      return currentBootstrap ? copyBootstrap(currentBootstrap) : null;
    },
  };
}
