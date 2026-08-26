export type AtendimentoColumn = "novo" | "em_andamento" | "concluido";

export type AtendimentoPriority = "critica" | "alta" | "media" | "baixa";

export type AtendimentoCard = {
  id: string;
  companyId: string;
  title: string;
  description: string;
  column: AtendimentoColumn;
  version: number;
  assigneeMembershipId: string | null;
  /** Campos da fatia de usabilidade; ausentes em respostas antigas. */
  priority?: AtendimentoPriority | null;
  categoryRefId?: string | null;
  channelRefId?: string | null;
  slaDueAt?: string | null;
  overdue?: boolean;
  requesterPersonRef?: string | null;
  watcherMembershipIds?: string[] | null;
};

export type QueueFilter = {
  status?: AtendimentoColumn;
  priority?: AtendimentoPriority;
  assigneeMembershipId?: string;
  overdue?: boolean;
  search?: string;
};

export type PersonOption = {
  employeeId: string;
  membershipId: string;
  displayName: string;
};

export type TimelineEvent = {
  id: string;
  eventSeq: number;
  actionKind:
    | "created"
    | "assigned"
    | "moved"
    | "fields_updated"
    | "watcher_added"
    | "watcher_removed";
  actorMembershipId: string | null;
  actorDisplayName: string;
  occurredAt: string;
};

export type LookupCategory = { id: string; name: string; isActive: boolean };

export type LookupChannel = { id: string; key: string; isActive: boolean };

export type LookupsResponse = {
  categories: LookupCategory[];
  channels: LookupChannel[];
};

export type UpdateFieldsInput = {
  priority?: AtendimentoPriority | null;
  categoryRefId?: string | null;
  channelRefId?: string | null;
  slaDueAt?: string | null;
  requesterPersonRef?: string | null;
  mentionedMembershipIds?: string[];
};

type CommandResult =
  | { ok: true; card: AtendimentoCard }
  | { ok: false; code: string; current?: AtendimentoCard };

const basePath = "/atendimento/cards";
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

function query(params: Record<string, string | undefined>) {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value) search.set(key, value);
  }
  const encoded = search.toString();
  return encoded ? `?${encoded}` : "";
}

/** Filtros viram query params compartilháveis na própria rota do quadro. */
export function queueFilterToQuery(filter: QueueFilter): string {
  return query({
    assigneeMembershipId: filter.assigneeMembershipId,
    overdue: filter.overdue ? "true" : undefined,
    priority: filter.priority,
    search: filter.search?.trim() || undefined,
    status: filter.status,
  });
}

async function command(path: string, body: object): Promise<CommandResult> {
  try {
    const requestToken = await getAntiforgeryToken();
    if (!requestToken) return { code: "atendimento_unavailable", ok: false };

    const response = await request(path, {
      body: JSON.stringify(body),
      headers: {
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: requestToken,
      },
      method: "POST",
    });
    if (response.ok)
      return { card: (await response.json()) as AtendimentoCard, ok: true };
    const error = (await response.json().catch(() => null)) as {
      code?: string;
      current?: AtendimentoCard;
    } | null;
    return {
      code: error?.code ?? "atendimento_unavailable",
      current: error?.current,
      ok: false,
    };
  } catch {
    return { code: "atendimento_unavailable", ok: false };
  }
}

export const atendimentoClient = {
  async list(
    companyId: string,
    filter: QueueFilter = {},
  ): Promise<AtendimentoCard[] | null> {
    try {
      const response = await request(
        `${basePath}?companyId=${encodeURIComponent(companyId)}${queueFilterToQuery(filter)}`,
      );
      return response.ok
        ? ((await response.json()) as AtendimentoCard[])
        : null;
    } catch {
      return null;
    }
  },
  create(companyId: string, title: string, description: string) {
    return command(basePath, {
      companyId,
      description,
      requestId: crypto.randomUUID(),
      title,
    });
  },
  assign(
    companyId: string,
    card: Pick<AtendimentoCard, "id" | "version">,
    assigneeMembershipId: string,
  ) {
    return command(`${basePath}/${card.id}/assign`, {
      assigneeMembershipId,
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },
  move(
    companyId: string,
    card: Pick<AtendimentoCard, "id" | "version">,
    column: AtendimentoColumn,
  ) {
    return command(`${basePath}/${card.id}/move`, {
      column,
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },
  fields(
    companyId: string,
    card: Pick<AtendimentoCard, "id" | "version">,
    input: UpdateFieldsInput & { mentionedMembershipIds?: string[] },
  ) {
    // undefined = inalterado; null só existe para explicitar limpeza futura.
    const payload: Record<string, unknown> = {
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    };
    for (const [key, value] of Object.entries(input)) {
      if (value !== undefined && !(typeof value === "string" && !value)) {
        payload[key] = value;
      }
    }
    return command(`${basePath}/${card.id}/fields`, payload);
  },
  addWatcher(
    companyId: string,
    card: Pick<AtendimentoCard, "id" | "version">,
    memberMembershipId: string,
  ) {
    return command(`${basePath}/${card.id}/watchers/add`, {
      companyId,
      expectedVersion: card.version,
      memberMembershipId,
      requestId: crypto.randomUUID(),
    });
  },
  removeWatcher(
    companyId: string,
    card: Pick<AtendimentoCard, "id" | "version">,
    memberMembershipId: string,
  ) {
    return command(`${basePath}/${card.id}/watchers/remove`, {
      companyId,
      expectedVersion: card.version,
      memberMembershipId,
      requestId: crypto.randomUUID(),
    });
  },
  async events(cardId: string, companyId: string): Promise<TimelineEvent[] | null> {
    try {
      const response = await request(
        `${basePath}/${cardId}/events?companyId=${encodeURIComponent(companyId)}`,
      );
      return response.ok
        ? ((await response.json()) as TimelineEvent[])
        : null;
    } catch {
      return null;
    }
  },
  async lookups(companyId: string): Promise<LookupsResponse | null> {
    try {
      const response = await request(
        `/atendimento/lookups?companyId=${encodeURIComponent(companyId)}`,
      );
      return response.ok
        ? ((await response.json()) as LookupsResponse)
        : null;
    } catch {
      return null;
    }
  },
  async directoryOptions(companyId: string): Promise<PersonOption[] | null> {
    try {
      const response = await request(
        `/foundation/companies/${encodeURIComponent(companyId)}/directory-options`,
      );
      return response.ok
        ? ((await response.json()) as PersonOption[])
        : null;
    } catch {
      return null;
    }
  },
  async createCategory(
    companyId: string,
    name: string,
  ): Promise<{ ok: boolean; code?: string }> {
    try {
      const requestToken = await getAntiforgeryToken();
      if (!requestToken) return { code: "atendimento_unavailable", ok: false };
      const response = await request("/atendimento/categories", {
        body: JSON.stringify({
          companyId,
          name: name.trim(),
          requestId: crypto.randomUUID(),
        }),
        headers: {
          "Content-Type": "application/json",
          [antiforgeryHeaderName]: requestToken,
        },
        method: "POST",
      });
      if (response.ok) return { ok: true };
      const error = (await response.json().catch(() => null)) as {
        code?: string;
      } | null;
      return { code: error?.code ?? "atendimento_unavailable", ok: false };
    } catch {
      return { code: "atendimento_unavailable", ok: false };
    }
  },
  async deactivateCategory(
    companyId: string,
    categoryId: string,
  ): Promise<{ ok: boolean; code?: string }> {
    try {
      const requestToken = await getAntiforgeryToken();
      if (!requestToken) return { code: "atendimento_unavailable", ok: false };
      const response = await request(
        `/atendimento/categories/${categoryId}/deactivate`,
        {
          body: JSON.stringify({
            companyId,
            requestId: crypto.randomUUID(),
          }),
          headers: {
            "Content-Type": "application/json",
            [antiforgeryHeaderName]: requestToken,
          },
          method: "POST",
        },
      );
      if (response.ok) return { ok: true };
      const error = (await response.json().catch(() => null)) as {
        code?: string;
      } | null;
      return { code: error?.code ?? "atendimento_unavailable", ok: false };
    } catch {
      return { code: "atendimento_unavailable", ok: false };
    }
  },
};
