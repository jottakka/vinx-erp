import { z } from "zod";
import { amountToCents, maximumAmountCents } from "./paguai-amount";
import { isValidDateOnly } from "./paguai-format";

const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const draftsPath = "/paguai/requests";
const proposalsPath = "/paguai/draft-proposals";
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
    version: z.literal(1),
    createdAt: z.string().datetime({ offset: true }),
  })
  .strict();
const proposalSchema = z
  .object({
    id: z.uuid(),
    companyId: z.uuid(),
    title: z.string().trim().min(1).max(200),
    description: z.string().trim().min(1).max(4000),
    amount: amountSchema,
    currency: z.literal("BRL"),
    dueDate: dateSchema,
    beneficiaryDisplayName: z.string().trim().min(1).max(200),
    proofRequirement: z.enum(["optional", "required"]),
    invoiceFileId: z.uuid(),
    confidence: z.number().min(0).max(1),
    sourceReferences: z.array(
      z
        .object({ field: z.string().min(1), source: z.string().min(1) })
        .strict(),
    ),
    state: z.enum(["pending", "consumed", "rejected", "invalidated"]),
    createdAt: z.string().datetime({ offset: true }),
    expiresAt: z.string().datetime({ offset: true }),
  })
  .strict();
const errorSchema = z.object({ code: z.string() }).strict();
const antiforgerySchema = z
  .object({ requestToken: z.string().min(1) })
  .strict();

export type PaguaiDraft = z.infer<typeof draftSchema>;
export type PaguaiProposal = z.infer<typeof proposalSchema>;
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
>;
export type PaguaiDraftError =
  | 400
  | 401
  | 403
  | 404
  | 409
  | 429
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
  if ([401, 429].includes(response.status)) return response.status as 401 | 429;
  if ([400, 403, 404, 409, 503].includes(response.status)) {
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
    async getProposal(
      companyId: string,
      proposalId: string,
    ): Promise<PaguaiResult<PaguaiProposal>> {
      try {
        const response = await request(
          `${proposalsPath}/${encodeURIComponent(proposalId)}?companyId=${encodeURIComponent(companyId)}`,
          { method: "GET" },
          fetchImplementation,
        );
        if (!response.ok)
          return { ok: false, error: await readError(response) };
        const parsed = proposalSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true, value: parsed.data }
          : { ok: false, error: "protocol" };
      } catch {
        return { ok: false, error: "network" };
      }
    },
    async confirmProposal(
      companyId: string,
      proposalId: string,
      requestId: string,
    ): Promise<PaguaiResult<PaguaiDraft>> {
      return mutateProposal(
        companyId,
        proposalId,
        requestId,
        "confirm",
        draftSchema,
      );
    },
    async rejectProposal(
      companyId: string,
      proposalId: string,
      requestId: string,
    ): Promise<PaguaiResult<void>> {
      return mutateProposal(companyId, proposalId, requestId, "reject");
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

  async function mutateProposal<T>(
    companyId: string,
    proposalId: string,
    requestId: string,
    operation: "confirm" | "reject",
    responseSchema?: z.ZodType<T>,
  ): Promise<PaguaiResult<T extends undefined ? void : T>> {
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
        `${proposalsPath}/${encodeURIComponent(proposalId)}/${operation}`,
        {
          body: JSON.stringify({ companyId, requestId }),
          headers: {
            Accept: "application/json",
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": token.data.requestToken,
          },
          method: "POST",
        },
        fetchImplementation,
      );
      if (!response.ok) return { ok: false, error: await readError(response) };
      if (!responseSchema) return { ok: true, value: undefined as never };
      const parsed = responseSchema.safeParse(await response.json());
      return parsed.success
        ? { ok: true, value: parsed.data as never }
        : { ok: false, error: "protocol" };
    } catch {
      return { ok: false, error: "network" };
    }
  }
}
