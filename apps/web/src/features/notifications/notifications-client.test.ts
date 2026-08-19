import { describe, expect, it, vi } from "vitest";
import { createNotificationsClient } from "./notifications-client";

const page = {
  items: [
    {
      id: "33333333-3333-4333-8333-333333333333",
      companyId: "11111111-1111-4111-8111-111111111111",
      source: "Sistema",
      event: null,
      createdAt: "2026-08-19T12:00:00.000+00:00",
      readAt: null,
      action: null,
    },
  ],
  nextCursor: null,
};

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    headers: { "Content-Type": "application/json" },
    status,
  });
}

describe("notifications client", () => {
  it("fetches an inbox page with cursor and limit", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(page));
    const client = createNotificationsClient(
      fetchMock as unknown as typeof fetch,
    );

    const result = await client.fetchInboxPage("a-token", 20);

    expect(result.ok).toBe(true);
    const [path] = fetchMock.mock.calls[0] as [string];
    expect(path).toBe("/notifications/inbox?limit=20&cursor=a-token");
  });

  it("fetches the unread count", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse({ unreadCount: 3 }));
    const client = createNotificationsClient(
      fetchMock as unknown as typeof fetch,
    );

    const result = await client.fetchUnreadCount();

    expect(result).toEqual({ ok: true, value: { unreadCount: 3 } });
  });

  it("marks a notification as read with an antiforgery token", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ requestToken: "token-1" }))
      .mockResolvedValueOnce(jsonResponse({ status: "read" }));
    const client = createNotificationsClient(
      fetchMock as unknown as typeof fetch,
    );

    const result = await client.markNotificationRead(
      "33333333-3333-4333-8333-333333333333",
    );

    expect(result).toEqual({ ok: true, value: { status: "read" } });
    const [readPath, readInit] = fetchMock.mock.calls[1] as [
      string,
      RequestInit,
    ];
    expect(readPath).toBe(
      "/notifications/inbox/33333333-3333-4333-8333-333333333333/read",
    );
    expect(readInit.method).toBe("POST");
    expect((readInit.headers as Record<string, string>)["X-CSRF-TOKEN"]).toBe(
      "token-1",
    );
  });

  it("maps http errors and network failures to typed errors", async () => {
    const unauthorized = createNotificationsClient(
      vi
        .fn()
        .mockResolvedValue(
          new Response(null, { status: 401 }),
        ) as unknown as typeof fetch,
    );
    const unavailable = createNotificationsClient(
      vi
        .fn()
        .mockResolvedValue(
          jsonResponse({ code: "notifications_unavailable" }, 503),
        ) as unknown as typeof fetch,
    );
    const offline = createNotificationsClient(
      vi
        .fn()
        .mockRejectedValue(new TypeError("offline")) as unknown as typeof fetch,
    );

    expect((await unauthorized.fetchUnreadCount()).ok).toBe(false);
    expect((await unavailable.fetchUnreadCount()).ok).toBe(false);
    expect((await offline.fetchUnreadCount()).ok).toBe(false);
  });

  it("rejects payloads outside the strict envelope", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse({ items: [], nextCursor: 7 }));
    const client = createNotificationsClient(
      fetchMock as unknown as typeof fetch,
    );

    const result = await client.fetchInboxPage(null, 20);

    expect(result).toEqual({ ok: false, error: "protocol" });
  });
});
