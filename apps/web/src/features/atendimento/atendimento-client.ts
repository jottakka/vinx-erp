import { z } from "zod";
import {
  fetchSameOriginBff,
  isCanonicalUuid,
} from "../../shared/http/bff-response";

export type AtendimentoColumn =
  | "novo"
  | "em_andamento"
  | "concluido"
  | "cancelado";

export type AtendimentoPriority = "critica" | "alta" | "media" | "baixa";

export type AtendimentoCard = {
  id: string;
  companyId: string;
  title: string;
  description: string;
  column: AtendimentoColumn;
  version: number;
  assigneeMembershipId: string | null;
  attachmentFileId: string | null;
  participantMembershipIds: readonly string[];
  priority: AtendimentoPriority;
  channel: AtendimentoChannel;
  slaDueAt: string | null;
  isOverdue: boolean;
  createdAt: string | null;
  updatedAt: string | null;
  /** Campos de ticket da fatia P6 (backend, PR 336); nulos quando indefinidos. */
  categoryRefId: string | null;
  channelRefId: string | null;
  requesterPersonRef: string | null;
  watcherMembershipIds: string[] | null;
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
    | "mentioned"
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

export type AtendimentoChannel = "whatsapp" | "email" | "web" | "manual";

export type AtendimentoAssignmentOption = {
  membershipId: string;
  displayName: string;
};

export type AtendimentoCreateOptions = {
  attachmentFileId?: string | null;
  participantMembershipIds?: readonly string[];
  priority?: AtendimentoPriority;
  channel?: AtendimentoChannel;
  slaDueAt?: string | null;
};

const atendimentoIdSchema = z.string().refine(isCanonicalUuid);

const atendimentoCardSchema = z
  .object({
    id: atendimentoIdSchema,
    companyId: atendimentoIdSchema,
    title: z.string().min(1).max(200),
    description: z.string().min(1).max(4000),
    column: z.enum(["novo", "em_andamento", "concluido", "cancelado"]),
    version: z.number().int().positive(),
    assigneeMembershipId: atendimentoIdSchema.nullable(),
    attachmentFileId: atendimentoIdSchema.nullable(),
    participantMembershipIds: z.array(atendimentoIdSchema),
    priority: z.enum(["critica", "alta", "media", "baixa"]),
    channel: z.enum(["whatsapp", "email", "web", "manual"]),
    slaDueAt: z.string().datetime({ offset: true }).nullable(),
    isOverdue: z.boolean(),
    createdAt: z.string().datetime({ offset: true }).nullable(),
    updatedAt: z.string().datetime({ offset: true }).nullable(),
    categoryRefId: atendimentoIdSchema.nullable(),
    channelRefId: atendimentoIdSchema.nullable(),
    requesterPersonRef: z.string().max(200).nullable(),
    watcherMembershipIds: z.array(atendimentoIdSchema).nullable(),
  })
  .strict();
const atendimentoCardsSchema = z.array(atendimentoCardSchema);

function parseCard(value: unknown): AtendimentoCard | null {
  const parsed = atendimentoCardSchema.safeParse(value);
  return parsed.success ? parsed.data : null;
}

type CommandResult =
  | { ok: true; card: AtendimentoCard }
  | {
      ok: false;
      code: string;
      current?: AtendimentoCard;
      mayHaveCommitted?: true;
    };

const basePath = "/atendimento/cards";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

const assignmentOptionsSchema = z.array(
  z
    .object({
      membershipId: atendimentoIdSchema,
      displayName: z.string().min(1),
    })
    .strict(),
);

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
  const requestToken = await getAntiforgeryToken();
  if (!requestToken) return { code: "atendimento_unavailable", ok: false };

  // Falha de transporte após o disparo do POST e 2xx com corpo que não valida
  // podem representar um commit no servidor: o resultado é ambíguo
  // (mayHaveCommitted), nunca uma falha definitiva, e a página reconcilia por
  // readback em vez de reenviar.
  const outcome = await fetchSameOriginBff(
    fetch,
    path,
    {
      body: JSON.stringify(body),
      headers: {
        "Content-Type": "application/json",
        [antiforgeryHeaderName]: requestToken,
      },
      mayHaveCommittedOnAbort: true,
      method: "POST",
    },
    (kind, { mayHaveCommittedOnAbort }) => ({
      kind,
      mayHaveCommitted: mayHaveCommittedOnAbort,
    }),
  );
  if (!("response" in outcome)) {
    return {
      code: "atendimento_unavailable",
      ...(outcome.mayHaveCommitted ? { mayHaveCommitted: true } : {}),
      ok: false,
    };
  }
  const response = outcome.response;
  if (response.ok) {
    const card = parseCard(await response.json().catch(() => null));
    return card
      ? { card, ok: true }
      : {
          code: "atendimento_unavailable",
          mayHaveCommitted: true,
          ok: false,
        };
  }
  const error = (await response.json().catch(() => null)) as {
    code?: string;
    current?: unknown;
  } | null;
  const current =
    error?.current === undefined ? undefined : parseCard(error.current);
  return {
    code: error?.code ?? "atendimento_unavailable",
    ...(current ? { current } : {}),
    ok: false,
  };
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
      if (!response.ok) return null;
      const parsed = atendimentoCardsSchema.safeParse(await response.json());
      return parsed.success ? parsed.data : null;
    } catch {
      return null;
    }
  },
  async assignmentOptions(
    companyId: string,
    signal?: AbortSignal,
  ): Promise<AtendimentoAssignmentOption[] | null> {
    try {
      const response = await request(
        `/atendimento/assignment-options?companyId=${encodeURIComponent(companyId)}`,
        { signal },
      );
      if (!response.ok) return null;
      const parsed = assignmentOptionsSchema.safeParse(await response.json());
      return parsed.success ? parsed.data : null;
    } catch {
      return null;
    }
  },
  create(
    companyId: string,
    requestId: string,
    title: string,
    description: string,
    {
      attachmentFileId = null,
      participantMembershipIds = [],
      priority = "media",
      channel = "web",
      slaDueAt = null,
    }: AtendimentoCreateOptions = {},
  ) {
    return command(basePath, {
      attachmentFileId,
      channel,
      companyId,
      description,
      participantMembershipIds: [...participantMembershipIds],
      priority,
      requestId,
      slaDueAt,
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
  async events(
    cardId: string,
    companyId: string,
  ): Promise<TimelineEvent[] | null> {
    try {
      const response = await request(
        `${basePath}/${cardId}/events?companyId=${encodeURIComponent(companyId)}`,
      );
      return response.ok ? ((await response.json()) as TimelineEvent[]) : null;
    } catch {
      return null;
    }
  },
  async lookups(companyId: string): Promise<LookupsResponse | null> {
    try {
      const response = await request(
        `/atendimento/lookups?companyId=${encodeURIComponent(companyId)}`,
      );
      return response.ok ? ((await response.json()) as LookupsResponse) : null;
    } catch {
      return null;
    }
  },
  async directoryOptions(companyId: string): Promise<PersonOption[] | null> {
    try {
      const response = await request(
        `/foundation/companies/${encodeURIComponent(companyId)}/directory-options`,
      );
      return response.ok ? ((await response.json()) as PersonOption[]) : null;
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
  cancel(companyId: string, card: AtendimentoCard, reason: string) {
    return command(`${basePath}/${card.id}/cancel`, {
      companyId,
      expectedVersion: card.version,
      reason: reason.trim(),
      requestId: crypto.randomUUID(),
    });
  },
};
