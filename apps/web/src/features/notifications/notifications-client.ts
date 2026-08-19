import { z } from "zod";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const inboxPath = "/notifications/inbox";

const actionSchema = z
  .object({ kind: z.string().min(1), href: z.string().min(1).startsWith("/") })
  .strict();

const inboxItemSchema = z
  .object({
    id: z.uuid(),
    companyId: z.uuid(),
    source: z.string().min(1),
    event: z.string().min(1).nullable(),
    createdAt: z.string().datetime({ offset: true }),
    readAt: z.string().datetime({ offset: true }).nullable(),
    action: actionSchema.nullable(),
  })
  .strict();

const inboxPageSchema = z
  .object({
    items: z.array(inboxItemSchema),
    nextCursor: z.string().nullable(),
  })
  .strict();

const unreadCountSchema = z
  .object({ unreadCount: z.number().int().min(0) })
  .strict();

const markReadSchema = z.object({ status: z.string() }).strict();

const errorSchema = z.object({ code: z.string() }).strict();
const antiforgerySchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();

export type NotificationInboxAction = z.infer<typeof actionSchema>;
export type NotificationInboxItem = z.infer<typeof inboxItemSchema>;
export type NotificationInboxPage = z.infer<typeof inboxPageSchema>;
export type NotificationsError =
  | 400
  | 401
  | 404
  | 429
  | 503
  | "network"
  | "protocol";
export type NotificationsResult<T> =
  | { ok: true; value: T }
  | { ok: false; error: NotificationsError };

function request(
  path: string,
  init: RequestInit,
  fetchImplementation: typeof fetch,
) {
  return fetchImplementation(path, {
    ...init,
    cache: "no-store",
    credentials: "same-origin",
    mode: "same-origin",
    redirect: "error",
  });
}

async function readError(response: Response): Promise<NotificationsError> {
  if ([401, 429].includes(response.status)) return response.status as 401 | 429;
  if ([400, 404, 503].includes(response.status)) {
    try {
      if (errorSchema.safeParse(await response.json()).success)
        return response.status as NotificationsError;
    } catch {
      return "protocol";
    }
  }
  return "protocol";
}

export function createNotificationsClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
) {
  return {
    async fetchInboxPage(
      cursor: string | null,
      limit: number,
    ): Promise<NotificationsResult<NotificationInboxPage>> {
      try {
        const query = new URLSearchParams({ limit: String(limit) });
        if (cursor) query.set("cursor", cursor);
        const response = await request(
          `${inboxPath}?${query.toString()}`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = inboxPageSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async fetchUnreadCount(): Promise<
      NotificationsResult<{ unreadCount: number }>
    > {
      try {
        const response = await request(
          `${inboxPath}/unread-count`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = unreadCountSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async markNotificationRead(
      deliveryId: string,
    ): Promise<NotificationsResult<{ status: string }>> {
      try {
        const tokenResponse = await request(
          antiforgeryPath,
          { method: "GET", headers: { Accept: "application/json" } },
          fetchImplementation,
        );
        if (!tokenResponse.ok)
          return { ok: false, error: await readError(tokenResponse) };
        const token = antiforgerySchema.safeParse(await tokenResponse.json());
        if (!token.success) return { ok: false, error: "protocol" };
        const response = await request(
          `${inboxPath}/${encodeURIComponent(deliveryId)}/read`,
          {
            headers: { "X-CSRF-TOKEN": token.data.requestToken },
            method: "POST",
          },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = markReadSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
  };
}

export type NotificationsClient = ReturnType<typeof createNotificationsClient>;
