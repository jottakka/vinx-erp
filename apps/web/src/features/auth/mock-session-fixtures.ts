import type { BootstrapSnapshot } from "./session-contract";

export const unavailableDevelopmentProfileMessage =
  "Perfil de desenvolvimento indisponível";

export const mockSessionProfiles: Readonly<Record<string, BootstrapSnapshot>> =
  {
    maria: {
      user: {
        id: "maria",
        name: "Maria",
      },
      companies: [
        {
          id: "alfa",
          name: "Alfa",
          capabilities: ["prototype.view"],
        },
        {
          id: "beta",
          name: "Beta",
          capabilities: ["prototype.view"],
        },
      ],
      flags: {
        prototypeSession: true,
      },
    },
  };
