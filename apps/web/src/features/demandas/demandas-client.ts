export type DemandStage =
  | "initiation"
  | "execution"
  | "awaiting_approval"
  | "closed";

export type DemandPriority = "critical" | "high" | "medium" | "low";

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

export type CommandResult<Demand> =
  | { ok: true; demand?: Demand; routine?: unknown }
  | {
      ok: false;
      code: string;
      current?: { version: number; stage: DemandStage };
    };

const basePath = "/demandas";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

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
      return response.ok ? ((await response.json()) as DemandBoardPage) : null;
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
      return response.ok ? ((await response.json()) as DemandDetail) : null;
    } catch {
      return null;
    }
  },

  async command<TDemand>(
    path: string,
    body: object,
  ): Promise<CommandResult<TDemand>> {
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
        const payload = await response.json();
        return typeof payload === "object" && payload !== null
          ? { ok: true as const, ...(payload as object) }
          : { ok: true as const };
      }
      const error = (await response.json().catch(() => null)) as {
        code?: string;
        current?: { version: number; stage: DemandStage };
      } | null;
      return {
        code: error?.code ?? "demandas_unavailable",
        current: error?.current,
        ok: false,
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
    return this.command<DemandBoardCard>("/demands", {
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
    return this.command<DemandBoardCard>(`/demands/${card.id}/advance`, {
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
    return this.command<DemandBoardCard>(
      `/demands/${demandId}/checklist-items/complete`,
      {
        companyId,
        expectedVersion: version,
        itemId,
        requestId: crypto.randomUUID(),
      },
    );
  },

  reassign(
    companyId: string,
    card: DemandBoardCard,
    newResponsibleMembershipId: string,
  ) {
    return this.command<DemandBoardCard>(`/demands/${card.id}/responsible`, {
      companyId,
      expectedVersion: card.version,
      newResponsibleMembershipId,
      requestId: crypto.randomUUID(),
    });
  },

  pause(companyId: string, card: DemandBoardCard) {
    return this.command<DemandBoardCard>(`/demands/${card.id}/pause`, {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },

  resume(companyId: string, card: DemandBoardCard) {
    return this.command<DemandBoardCard>(`/demands/${card.id}/resume`, {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },

  resumeOverdue(companyId: string, card: DemandBoardCard, newDueAt: string) {
    return this.command<DemandBoardCard>(`/demands/${card.id}/resume-overdue`, {
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
    return this.command<DemandBoardCard>(`/demands/${demandId}/participants`, {
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
    return this.command<DemandBoardCard>(
      `/demands/${demandId}/approval/${approve ? "approve" : "reject"}`,
      {
        companyId,
        expectedVersion: version,
        requestId: crypto.randomUUID(),
      },
    );
  },
};
