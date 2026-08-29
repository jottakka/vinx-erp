import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import type { NotificationInboxItem } from "./notifications-client";
import {
  NotificationsPage,
  type NotificationsPageProps,
} from "./notifications-page";
import {
  NotificationsProvider,
  useNotifications,
} from "./notifications-provider";

const companyId = "11111111-1111-4111-8111-111111111111";

const item: NotificationInboxItem = {
  id: "33333333-3333-4333-8333-333333333333",
  companyId,
  source: "Sistema",
  event: null,
  createdAt: "2026-08-19T12:00:00.000+00:00",
  readAt: null,
  action: { kind: "paguai", href: "/paguai" },
};

const bootstrap: BootstrapSnapshot = {
  companies: [
    {
      membershipId: "44444444-4444-4444-8444-444444444444",
      companyId,
      companyName: "Alfa",
      capabilities: [],
    },
  ],
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

function CounterProbe() {
  const { unreadCount, refreshUnreadCount } = useNotifications();
  return (
    <button
      data-testid="refresh"
      onClick={() => void refreshUnreadCount()}
      type="button"
    >
      {unreadCount ?? "null"}
    </button>
  );
}

function renderPage(client: NotificationsPageProps["notificationsClient"]) {
  return render(
    <VinxThemeProvider>
      <MemoryRouter>
        <SessionProvider gateway={gateway}>
          <NotificationsProvider
            client={
              {
                fetchUnreadCount: vi
                  .fn()
                  .mockResolvedValue({ ok: true, value: { unreadCount: 1 } }),
              } as never
            }
          >
            <CounterProbe />
            <NotificationsPage notificationsClient={client} />
          </NotificationsProvider>
        </SessionProvider>
      </MemoryRouter>
    </VinxThemeProvider>,
  );
}

describe("notifications page", () => {
  it("lists authorized notifications with the company of origin", async () => {
    const client = {
      fetchInboxPage: vi.fn().mockResolvedValue({
        ok: true,
        value: { items: [item], nextCursor: null },
      }),
      fetchUnreadCount: vi.fn(),
      markNotificationRead: vi.fn(),
    };
    renderPage(client as never);

    expect(
      await screen.findByRole("heading", { name: "Notificações" }),
    ).toBeVisible();
    expect(await screen.findByText("Alfa")).toBeVisible();
    expect(screen.getByText("Não lida")).toBeVisible();
    expect(
      screen.getByRole("link", { name: /abrir origem: alfa/i }),
    ).toHaveAttribute("href", "/paguai");
  });

  it("renders the canonical demandas deep link and drops malformed selectors", async () => {
    const demandasItem: NotificationInboxItem = {
      ...item,
      id: "66666666-6666-6666-8666-666666666666",
      action: {
        kind: "demandas",
        href: "/demandas?demand=77777777-7777-7777-8777-777777777777",
      },
    };
    const malformedItem: NotificationInboxItem = {
      ...item,
      id: "88888888-8888-8888-8888-888888888888",
      action: { kind: "demandas", href: "/demandas?demand=/../../evil" },
    };
    const client = {
      fetchInboxPage: vi.fn().mockResolvedValue({
        ok: true,
        value: { items: [demandasItem, malformedItem], nextCursor: null },
      }),
      fetchUnreadCount: vi.fn(),
      markNotificationRead: vi.fn(),
    };
    renderPage(client as never);

    expect(
      await screen.findByRole("link", {
        name: /abrir origem: alfa/i,
      }),
    ).toHaveAttribute(
      "href",
      "/demandas?demand=77777777-7777-7777-8777-777777777777",
    );
    // Deep link fora da allowlist canônica não vira ação clicável.
    const links = screen.queryAllByRole("link", { name: /abrir origem/i });
    expect(links).toHaveLength(1);
  });

  it("confirms the server result before rendering the item as read", async () => {
    const user = userEvent.setup();
    const client = {
      fetchInboxPage: vi.fn().mockResolvedValue({
        ok: true,
        value: { items: [item], nextCursor: null },
      }),
      fetchUnreadCount: vi.fn(),
      markNotificationRead: vi.fn().mockResolvedValue({
        ok: true,
        value: { status: "read" },
      }),
    };
    renderPage(client as never);

    const markButton = await screen.findByRole("button", {
      name: /marcar como lida: alfa: atualização \(/i,
    });
    await user.click(markButton);

    await waitFor(() =>
      expect(client.markNotificationRead).toHaveBeenCalledWith(item.id),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: /marcar como lida/i }),
      ).toBeNull(),
    );
  });

  it("keeps the item visibly unread and offers retry when the read fails", async () => {
    const user = userEvent.setup();
    const client = {
      fetchInboxPage: vi.fn().mockResolvedValue({
        ok: true,
        value: { items: [item], nextCursor: null },
      }),
      fetchUnreadCount: vi.fn(),
      markNotificationRead: vi
        .fn()
        .mockResolvedValueOnce({ ok: false, error: "network" })
        .mockResolvedValueOnce({ ok: true, value: { status: "read" } }),
    };
    renderPage(client as never);

    await screen.findByText("Não lida");
    await user.click(
      screen.getByRole("button", {
        name: /marcar como lida: alfa: atualização \(/i,
      }),
    );

    expect(
      await screen.findByText(
        "Não foi possível marcar como lida. A notificação continua não lida.",
      ),
    ).toBeVisible();
    expect(screen.getByText("Não lida")).toBeVisible();

    await user.click(
      screen.getByRole("button", { name: /repetir marcação: alfa/i }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole("button", {
          name: /marcar como lida: alfa: atualização \(/i,
        }),
      ).toBeNull(),
    );
  });

  it("shows loading, empty and error states safely", async () => {
    const user = userEvent.setup();
    const failing = {
      fetchInboxPage: vi
        .fn()
        .mockResolvedValueOnce({ ok: false, error: 503 })
        .mockResolvedValueOnce({
          ok: true,
          value: { items: [], nextCursor: null },
        }),
      fetchUnreadCount: vi.fn(),
      markNotificationRead: vi.fn(),
    };
    renderPage(failing as never);

    expect(
      await screen.findByText(/Notificações estão indisponíveis/i),
    ).toBeVisible();
    // O empty state não é afirmado junto com o erro: somente após uma
    // resposta bem-sucedida vazia.
    expect(screen.queryByText(/não tem notificações/i)).toBeNull();

    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByText(/Você não tem notificações/i)).toBeVisible();
  });
});
