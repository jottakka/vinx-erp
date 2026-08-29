import { z } from "zod";

export type DemandStage =
  | "initiation"
  | "execution"
  | "awaiting_approval"
  | "closed";

export type DemandPriority = "critical" | "high" | "medium" | "low";

export type DemandResponse = z.infer<typeof demandResponseSchema>;

export type DemandBoardPage = {
  items: DemandBoardCard[];
  total: number;
  hasMore: boolean;
  nextCursor: string | null;
};

export type DemandBoardCard = {
  id: string;
  demandNumber: number;
  demandDisplayNumber: string;
  title: string;
  priority: DemandPriority;
  stage: DemandStage;
  responsibleMembershipId: string;
  effectiveDeadline: string | null;
  stageStartedAt: string;
  isOverdue: boolean;
  automationPaused: boolean;
  version: number;
};

export type DemandDetail = {
  demand: {
    id: string;
    companyId: string;
    demandNumber: number;
    demandDisplayNumber: string;
    title: string;
    description: string;
    priority: DemandPriority;
    stage: DemandStage;
    routineId: string | null;
    responsibleMembershipId: string;
    tipo: string | null;
    departamento: string | null;
    manualDueAt: string | null;
    slaDueAt: string | null;
    stageDueAt: string | null;
    effectiveDeadline: string | null;
    automationPaused: boolean;
    isOverdue: boolean;
    cycleCount: number;
    version: number;
  };
  checklist: Array<{
    id: string;
    text: string;
    position: number;
    isDone: boolean;
    doneAt: string | null;
  }>;
  participants: Array<{ id: string; userId: string; role: string }>;
  history: Array<{
    id: string;
    kind: string;
    fromStage: string | null;
    toStage: string | null;
    cycleCount: number;
    actorUserId: string | null;
    occurredAt: string;
  }>;
};

export type CommandResult =
  | { ok: true; demand?: DemandResponse }
  | { ok: false; code: string; current?: { version: number; stage: string } };

const basePath = "/demandas";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

// Contrato estrito por operação: qualquer campo ausente, tipo errado ou
// propriedade extra em 2xx é falha de protocolo — o cliente não repassa
// payload não validado à UI.
const demandStageSchema = z.enum([
  "initiation",
  "execution",
  "awaiting_approval",
  "closed",
]);
const demandPrioritySchema = z.enum(["critical", "high", "medium", "low"]);

// DemandResponse completa do controller (contrato canônico de comandos e
// do campo demand do detalhe): sem stageStartedAt — esse campo só existe
// no card do board.
const demandResponseSchema = z
  .object({
    automationPaused: z.boolean(),
    companyId: z.string().uuid(),
    cycleCount: z.number().int().nonnegative(),
    departamento: z.string().nullable(),
    demandDisplayNumber: z.string(),
    demandNumber: z.number().int(),
    description: z.string(),
    effectiveDeadline: z.string().nullable(),
    id: z.string().uuid(),
    isOverdue: z.boolean(),
    manualDueAt: z.string().nullable(),
    priority: demandPrioritySchema,
    responsibleMembershipId: z.string().uuid(),
    routineId: z.string().nullable(),
    slaDueAt: z.string().nullable(),
    stage: demandStageSchema,
    stageDueAt: z.string().nullable(),
    title: z.string(),
    tipo: z.string().nullable(),
    version: z.number().int().nonnegative(),
  })
  .strict();

const demandBoardCardSchema = z
  .object({
    automationPaused: z.boolean(),
    demandDisplayNumber: z.string(),
    demandNumber: z.number().int(),
    effectiveDeadline: z.string().nullable(),
    id: z.string().uuid(),
    isOverdue: z.boolean(),
    priority: demandPrioritySchema,
    responsibleMembershipId: z.string().uuid(),
    stage: demandStageSchema,
    stageStartedAt: z.string(),
    title: z.string(),
    version: z.number().int().nonnegative(),
  })
  .strict();

const demandBoardPageSchema = z
  .object({
    hasMore: z.boolean(),
    items: z.array(demandBoardCardSchema),
    nextCursor: z.string().nullable(),
    total: z.number().int().nonnegative(),
  })
  .strict();

const demandDetailSchema = z
  .object({
    checklist: z.array(
      z
        .object({
          doneAt: z.string().nullable(),
          id: z.string().uuid(),
          isDone: z.boolean(),
          position: z.number().int(),
          text: z.string(),
        })
        .strict(),
    ),
    demand: demandResponseSchema,
    history: z.array(
      z
        .object({
          actorUserId: z.string().uuid().nullable(),
          cycleCount: z.number().int(),
          fromStage: z.string().nullable(),
          id: z.string().uuid(),
          kind: z.string(),
          occurredAt: z.string(),
          toStage: z.string().nullable(),
        })
        .strict(),
    ),
    participants: z.array(
      z
        .object({
          id: z.string().uuid(),
          role: z.string(),
          userId: z.string().uuid(),
        })
        .strict(),
    ),
  })
  .strict();

// Sucesso do comando: o controller devolve a DemandResponse DIRETA (sem
// envelope). Conflito: current é a DemandResponse atual — projeta o par que
// a UI consome depois de validar o objeto completo.
const commandSuccessSchema = demandResponseSchema;

const commandErrorSchema = z
  .object({
    code: z.string().min(1),
    current: demandResponseSchema.optional(),
  })
  .strict();

function request(path: string, init: RequestInit = {}) {
  return fetch(path, {
    ...init,
    cache: "no-store",
    credentials: "same-origin",
    mode: "same-origin",
    redirect: "error",
  });
}

async function getAntiforgeryToken(): Promise<string | null> {
  try {
    const response = await request(antiforgeryPath, {
      headers: { Accept: "application/json" },
      method: "GET",
    });
    if (!response.ok) return null;
    const payload = (await response.json()) as { requestToken?: unknown };
    return typeof payload.requestToken === "string" && payload.requestToken
      ? payload.requestToken
      : null;
  } catch {
    return null;
  }
}

export function demandDeepLink(demandId: string): string {
  return `${basePath}?demand=${encodeURIComponent(demandId)}`;
}

export const demandasClient = {
  async board(
    companyId: string,
    filters: {
      search?: string;
      priority?: string;
      responsibleMembershipId?: string;
      limit?: number;
      after?: string;
    } = {},
  ): Promise<DemandBoardPage | null> {
    try {
      const params = new URLSearchParams({ companyId });
      if (filters.search?.trim()) params.set("search", filters.search.trim());
      if (filters.priority) params.set("priority", filters.priority);
      if (filters.responsibleMembershipId?.trim())
        params.set(
          "responsibleMembershipId",
          filters.responsibleMembershipId.trim(),
        );
      if (filters.limit) params.set("limit", String(filters.limit));
      if (filters.after) params.set("after", filters.after);
      const response = await request(`${basePath}/board?${params.toString()}`);
      if (!response.ok) return null;
      const parsed = demandBoardPageSchema.safeParse(await response.json());
      return parsed.success ? parsed.data : null;
    } catch {
      return null;
    }
  },

  async detail(
    companyId: string,
    demandId: string,
  ): Promise<DemandDetail | null> {
    try {
      const response = await request(
        `${basePath}/demands/${encodeURIComponent(demandId)}?companyId=${encodeURIComponent(companyId)}`,
      );
      if (!response.ok) return null;
      const parsed = demandDetailSchema.safeParse(await response.json());
      return parsed.success ? parsed.data : null;
    } catch {
      return null;
    }
  },

  async command(path: string, body: object): Promise<CommandResult> {
    try {
      const requestToken = await getAntiforgeryToken();
      if (!requestToken) return { code: "demandas_unavailable", ok: false };
      const response = await request(`${basePath}${path}`, {
        body: JSON.stringify(body),
        headers: {
          "Content-Type": "application/json",
          [antiforgeryHeaderName]: requestToken,
        },
        method: "POST",
      });
      if (response.ok) {
        const parsed = commandSuccessSchema.safeParse(await response.json());
        return parsed.success
          ? { ok: true as const, demand: parsed.data }
          : { code: "demandas_unavailable", ok: false as const };
      }
      const error = commandErrorSchema.safeParse(
        await response.json().catch(() => null),
      );
      if (!error.success) return { code: "demandas_unavailable", ok: false };
      const { current, ...rest } = error.data;
      return {
        ...rest,
        ok: false as const,
        current: current
          ? { stage: current.stage, version: current.version }
          : undefined,
      };
    } catch {
      return { code: "demandas_unavailable", ok: false };
    }
  },

  create(input: {
    companyId: string;
    title: string;
    description: string;
    priority: DemandPriority;
    responsibleMembershipId: string;
    manualDueAt?: string;
  }) {
    return this.command("/demands", {
      companyId: input.companyId,
      description: input.description,
      priority: input.priority,
      requestId: crypto.randomUUID(),
      responsibleMembershipId: input.responsibleMembershipId,
      manualDueAt: input.manualDueAt,
      title: input.title,
    });
  },

  advance(companyId: string, card: DemandBoardCard) {
    return this.command(`/demands/${card.id}/advance`, {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },

  completeChecklistItem(
    companyId: string,
    demandId: string,
    version: number,
    itemId: string,
  ) {
    return this.command(`/demands/${demandId}/checklist-items/complete`, {
      companyId,
      expectedVersion: version,
      itemId,
      requestId: crypto.randomUUID(),
    });
  },

  reassign(
    companyId: string,
    card: DemandBoardCard,
    newResponsibleMembershipId: string,
  ) {
    return this.command(`/demands/${card.id}/responsible`, {
      companyId,
      expectedVersion: card.version,
      newResponsibleMembershipId,
      requestId: crypto.randomUUID(),
    });
  },

  pause(companyId: string, card: DemandBoardCard) {
    return this.command(`/demands/${card.id}/pause`, {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },

  resume(companyId: string, card: DemandBoardCard) {
    return this.command(`/demands/${card.id}/resume`, {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },

  resumeOverdue(companyId: string, card: DemandBoardCard, newDueAt: string) {
    return this.command(`/demands/${card.id}/resume-overdue`, {
      companyId,
      expectedVersion: card.version,
      newDueAt,
      requestId: crypto.randomUUID(),
    });
  },

  addParticipant(
    companyId: string,
    demandId: string,
    version: number,
    userId: string,
    asSupervisor: boolean,
  ) {
    return this.command(`/demands/${demandId}/participants`, {
      asSupervisor,
      companyId,
      expectedVersion: version,
      requestId: crypto.randomUUID(),
      userId,
    });
  },

  decideApproval(
    companyId: string,
    demandId: string,
    version: number,
    approve: boolean,
  ) {
    return this.command(
      `/demands/${demandId}/approval/${approve ? "approve" : "reject"}`,
      {
        companyId,
        expectedVersion: version,
        requestId: crypto.randomUUID(),
      },
    );
  },
};
