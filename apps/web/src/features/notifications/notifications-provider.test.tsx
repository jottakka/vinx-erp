import { act, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import {
  NotificationsProvider,
  useNotifications,
} from "./notifications-provider";

const bootstrap: BootstrapSnapshot = {
  companies: [],
  flags: {},
  platform: { isPlatformOperator: false },
  user: {
    id: "55555555-5555-4555-8555-555555555555",
    displayName: "Maria",
  },
};

function gatewayFor(snapshot: BootstrapSnapshot | null): MockSessionGateway {
  return {
    getBootstrap: async () => snapshot,
    login: async () => bootstrap,
    logout: async () => undefined,
  };
}

function UnreadCountProbe() {
  const { unreadCount } = useNotifications();
  return <span data-testid="unread-count">{unreadCount ?? "null"}</span>;
}

describe("notifications provider", () => {
  it("loads the unread count after a valid bootstrap", async () => {
    const fetchUnreadCount = vi.fn().mockResolvedValue({
      ok: true,
      value: { unreadCount: 4 },
    });
    render(
      <SessionProvider gateway={gatewayFor(bootstrap)}>
        <NotificationsProvider
          client={
            {
              fetchUnreadCount,
            } as never
          }
        >
          <UnreadCountProbe />
        </NotificationsProvider>
      </SessionProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("unread-count").textContent).toBe("4"),
    );
  });

  it("keeps the previous count when the request fails", async () => {
    const fetchUnreadCount = vi
      .fn()
      .mockResolvedValueOnce({ ok: true, value: { unreadCount: 2 } })
      .mockResolvedValueOnce({ ok: false, error: "network" });
    render(
      <SessionProvider gateway={gatewayFor(bootstrap)}>
        <NotificationsProvider
          client={
            {
              fetchUnreadCount,
            } as never
          }
        >
          <UnreadCountProbe />
        </NotificationsProvider>
      </SessionProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("unread-count").textContent).toBe("2"),
    );
    await act(async () => {
      window.dispatchEvent(new Event("focus"));
    });
    await waitFor(() => expect(fetchUnreadCount).toHaveBeenCalledTimes(2));
    // Falha de rede não reduz o contador para zero nem o apaga.
    expect(screen.getByTestId("unread-count").textContent).toBe("2");
  });

  it("resets the counter when the session ends", async () => {
    const fetchUnreadCount = vi.fn().mockResolvedValue({
      ok: true,
      value: { unreadCount: 1 },
    });
    const { rerender } = render(
      <SessionProvider gateway={gatewayFor(bootstrap)}>
        <NotificationsProvider client={{ fetchUnreadCount } as never}>
          <UnreadCountProbe />
        </NotificationsProvider>
      </SessionProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("unread-count").textContent).toBe("1"),
    );

    rerender(
      <SessionProvider gateway={gatewayFor(null)}>
        <NotificationsProvider client={{ fetchUnreadCount } as never}>
          <UnreadCountProbe />
        </NotificationsProvider>
      </SessionProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId("unread-count").textContent).toBe("null"),
    );
  });
});
