import { z } from "zod";
import { isCanonicalUuid } from "../../shared/http/bff-response";

export type AtendimentoColumn =
  | "novo"
  | "em_andamento"
  | "concluido"
  | "cancelado";

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
};

export type AtendimentoAssignmentOption = {
  membershipId: string;
  displayName: string;
};

export type AtendimentoCreateOptions = {
  attachmentFileId?: string | null;
  participantMembershipIds?: readonly string[];
};

type CommandResult =
  | { ok: true; card: AtendimentoCard }
  | { ok: false; code: string; current?: AtendimentoCard };

const basePath = "/atendimento/cards";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const antiforgeryHeaderName = "X-CSRF-TOKEN";

const atendimentoIdSchema = z.string().refine(isCanonicalUuid);
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
  async list(companyId: string): Promise<AtendimentoCard[] | null> {
    try {
      const response = await request(
        `${basePath}?companyId=${encodeURIComponent(companyId)}`,
      );
      return response.ok
        ? ((await response.json()) as AtendimentoCard[])
        : null;
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
    }: AtendimentoCreateOptions = {},
  ) {
    return command(basePath, {
      attachmentFileId,
      companyId,
      description,
      participantMembershipIds: [...participantMembershipIds],
      requestId,
      title,
    });
  },
  assign(
    companyId: string,
    card: AtendimentoCard,
    assigneeMembershipId: string,
  ) {
    return command(`${basePath}/${card.id}/assign`, {
      assigneeMembershipId,
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
  },
  move(companyId: string, card: AtendimentoCard, column: AtendimentoColumn) {
    return command(`${basePath}/${card.id}/move`, {
      column,
      companyId,
      expectedVersion: card.version,
      requestId: crypto.randomUUID(),
    });
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
