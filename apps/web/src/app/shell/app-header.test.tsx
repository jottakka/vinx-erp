import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../../features/auth/session-contract";
import { SessionProvider } from "../../features/auth/session-provider";
import { NotificationsProvider } from "../../features/notifications/notifications-provider";
import { AppHeader } from "./app-header";

const bootstrap: BootstrapSnapshot = {
  companies: [],
  flags: {},
  platform: { isPlatformOperator: false },
  user: {
    id: "55555555-5555-4555-8555-555555555555",
    displayName: "Maria",
  },
};

const gateway: MockSessionGateway = {
  getBootstrap: async () => bootstrap,
  login: async () => bootstrap,
  logout: async () => undefined,
};

function renderHeader(unreadCount: number | null) {
  render(
    <VinxThemeProvider>
      <MemoryRouter>
        <SessionProvider gateway={gateway}>
          <NotificationsProvider
            client={
              {
                fetchUnreadCount: vi
                  .fn()
                  .mockResolvedValue({ ok: true, value: { unreadCount } }),
              } as never
            }
          >
            <AppHeader
              isSigningOut={false}
              mobileNavigationOpen={false}
              onOpenNavigation={() => undefined}
              onSignOut={() => undefined}
              userName="Maria"
            />
          </NotificationsProvider>
        </SessionProvider>
      </MemoryRouter>
    </VinxThemeProvider>,
  );
}

describe("app header notifications badge", () => {
  it("announces the exact unread count after load", async () => {
    renderHeader(3);

    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: "Notificações: 3 não lidas" }),
      ).toBeVisible(),
    );
  });

  it("announces zero distinctly and hides the badge", async () => {
    renderHeader(0);

    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: "Notificações: nenhuma não lida" }),
      ).toBeVisible(),
    );
  });
});
