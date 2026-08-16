import { z } from "zod";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const draftsPath = "/paguai/requests";

const draftSchema = z
  .object({
    id: z.uuid(),
    companyId: z.uuid(),
    requestId: z.uuid(),
    title: z.string().trim().min(1).max(200),
    description: z.string().trim().min(1).max(4000),
    state: z.literal("rascunho"),
    version: z.literal(1),
    createdAt: z.string().datetime({ offset: true }),
  })
  .strict();
const errorSchema = z.object({ code: z.string() }).strict();
const antiforgerySchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();

export type PaguaiDraft = z.infer<typeof draftSchema>;
export type PaguaiDraftError =
  | 400
  | 401
  | 403
  | 404
  | 409
  | 503
  | "network"
  | "protocol";
export type PaguaiResult<T> =
  | { ok: true; value: T }
  | { ok: false; error: PaguaiDraftError };

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

async function readError(response: Response): Promise<PaguaiDraftError> {
  if ([400, 401, 403, 404, 409, 503].includes(response.status)) {
    try {
      if (errorSchema.safeParse(await response.json()).success)
        return response.status as PaguaiDraftError;
    } catch {
      return "protocol";
    }
  }
  return "protocol";
}

export function createPaguaiDraftClient(
  fetchImplementation: typeof fetch = globalThis.fetch,
) {
  return {
    async list(
      companyId: string,
    ): Promise<PaguaiResult<readonly PaguaiDraft[]>> {
      try {
        const response = await request(
          `${draftsPath}?companyId=${encodeURIComponent(companyId)}`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = z.array(draftSchema).safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async detail(
      companyId: string,
      id: string,
    ): Promise<PaguaiResult<PaguaiDraft>> {
      try {
        const response = await request(
          `${draftsPath}/${encodeURIComponent(id)}?companyId=${encodeURIComponent(companyId)}`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = draftSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async create(
      input: Omit<PaguaiDraft, "id" | "state" | "version" | "createdAt">,
    ): Promise<PaguaiResult<PaguaiDraft>> {
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
          draftsPath,
          {
            body: JSON.stringify(input),
            headers: {
              Accept: "application/json",
              "Content-Type": "application/json",
              "X-CSRF-TOKEN": token.data.requestToken,
            },
            method: "POST",
          },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = draftSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
  };
}
