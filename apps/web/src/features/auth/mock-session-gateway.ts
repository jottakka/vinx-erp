import {
  mockSessionProfiles,
  unavailableDevelopmentProfileMessage,
} from "./mock-session-fixtures";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";

function copyBootstrap(snapshot: BootstrapSnapshot): BootstrapSnapshot {
  return structuredClone(snapshot);
}

export function createMockSessionGateway(): MockSessionGateway {
  let currentBootstrap: BootstrapSnapshot | null = null;

  return {
    async login(profileId) {
      const profile = mockSessionProfiles[profileId];

      if (!profile) {
        throw new Error(unavailableDevelopmentProfileMessage);
      }

      currentBootstrap = copyBootstrap(profile);
      return copyBootstrap(currentBootstrap);
    },
    async logout() {
      currentBootstrap = null;
    },
    async getBootstrap() {
      return currentBootstrap ? copyBootstrap(currentBootstrap) : null;
    },
  };
}
