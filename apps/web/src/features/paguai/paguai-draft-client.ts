import { z } from "zod";
import { amountToCents, maximumAmountCents } from "./paguai-amount";
import { isValidDateOnly } from "./paguai-format";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const draftsPath = "/paguai/requests";
const amountSchema = z
  .string()
  .regex(/^\d+(?:\.\d{1,2})?$/)
  .refine((amount) => {
    const cents = amountToCents(amount);
    return cents !== null && cents > 0n && cents <= maximumAmountCents;
  });

const dateSchema = z.string().refine(isValidDateOnly);

const draftSchema = z
  .object({
    id: z.uuid(),
    companyId: z.uuid(),
    requestId: z.uuid(),
    title: z.string().trim().min(1).max(200),
    description: z.string().trim().min(1).max(4000),
    amount: amountSchema,
    currency: z.literal("BRL"),
    dueDate: dateSchema,
    beneficiaryDisplayName: z.string().trim().min(1).max(200),
    invoiceFileId: z.uuid().nullable(),
    proofRequirement: z.enum(["optional", "required"]),
    state: z.literal("rascunho"),
    version: z.number().int().min(1),
    createdAt: z.string().datetime({ offset: true }),
    proofFileId: z.uuid().nullish(),
    proofFileName: z.string().trim().min(1).max(255).nullish(),
    proofFileMimeType: z.string().trim().min(1).max(100).nullish(),
    proofFileSizeBytes: z.number().int().min(0).nullish(),
  })
  .strict();
const errorSchema = z.object({ code: z.string() }).strict();
const antiforgerySchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();
const proofLinkSchema = z
  .object({
    url: z.string().url(),
    expiresAt: z.string().datetime({ offset: true }),
  })
  .strict();

export type PaguaiProofLink = z.infer<typeof proofLinkSchema>;

export type PaguaiDraft = z.infer<typeof draftSchema>;
export type CreatePaguaiDraftInput = Pick<
  PaguaiDraft,
  | "companyId"
  | "requestId"
  | "title"
  | "description"
  | "amount"
  | "currency"
  | "dueDate"
  | "beneficiaryDisplayName"
  | "invoiceFileId"
  | "proofRequirement"
> & { proofFileId?: string | null };
export type PaguaiDraftError =
  | 400
  | 401
  | 403
  | 404
  | 409
  | 429
  | 503
  | { status: 400 | 403 | 409; code: string }
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
  if ([401, 429].includes(response.status)) return response.status as 401 | 429;
  if ([400, 403, 404, 409, 503].includes(response.status)) {
    try {
      const parsed = errorSchema.safeParse(await response.json());
      // Códigos tipados de comprovante chegam à página como objeto; os
      // demais códigos documentados mantêm o canal numérico legado.
      if (
        parsed.success &&
        response.status !== 503 &&
        parsed.data.code.startsWith("proof_")
      )
        return {
          status: response.status as 400 | 403 | 409,
          code: parsed.data.code,
        };
      if (parsed.success) return response.status as PaguaiDraftError;
      return "protocol";
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
    /**
     * Busca sob demanda a URL assinada de curta duração; a URL NUNCA é
     * persistida em estado ou payload — vale só para a abuição imediata.
     */
    async proofLink(
      companyId: string,
      id: string,
    ): Promise<PaguaiResult<PaguaiProofLink>> {
      try {
        const response = await request(
          `${draftsPath}/${encodeURIComponent(id)}/proof-link?companyId=${encodeURIComponent(companyId)}`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = proofLinkSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async create(
      input: CreatePaguaiDraftInput,
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
