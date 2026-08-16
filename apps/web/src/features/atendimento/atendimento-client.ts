export type AtendimentoColumn = "novo" | "em_andamento" | "concluido";

export type AtendimentoCard = {
  id: string;
  companyId: string;
  title: string;
  description: string;
  column: AtendimentoColumn;
  version: number;
  assigneeMembershipId: string | null;
};

type CommandResult =
  | { ok: true; card: AtendimentoCard }
  | { ok: false; code: string; current?: AtendimentoCard };

const basePath = "/atendimento/cards";

function request(path: string, init: RequestInit = {}) {
  return fetch(path, {
    ...init,
    cache: "no-store",
    credentials: "same-origin",
    mode: "same-origin",
    redirect: "error",
  });
}

async function command(path: string, body: object): Promise<CommandResult> {
  try {
    const response = await request(path, {
      body: JSON.stringify(body),
      headers: { "Content-Type": "application/json" },
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
};
