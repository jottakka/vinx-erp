import type {
  AtendimentoCard,
  AtendimentoChannel,
  AtendimentoPriority,
} from "./atendimento-client";

export const priorityPresentation = {
  critica: { label: "Crítica", tone: "error" },
  alta: { label: "Alta", tone: "warning" },
  media: { label: "Média", tone: "info" },
  baixa: { label: "Baixa", tone: "default" },
} as const satisfies Record<
  AtendimentoPriority,
  { label: string; tone: "error" | "warning" | "info" | "default" }
>;

type PriorityPresentation = {
  label: string;
  tone: "error" | "warning" | "info" | "default";
};

/** Estado neutro da prioridade: nula é válida no contrato do backend. */
export const unsetPriorityPresentation: PriorityPresentation = {
  label: "—",
  tone: "default",
};

export function priorityPresentationFor(
  priority: AtendimentoPriority | null,
): PriorityPresentation {
  return priority ? priorityPresentation[priority] : unsetPriorityPresentation;
}

export const channelLabels = {
  whatsapp: "WhatsApp",
  email: "E-mail",
  web: "Web",
  manual: "Manual",
} as const satisfies Record<AtendimentoChannel, string>;

const atendimentoDateTimeFormatter = new Intl.DateTimeFormat("pt-BR", {
  dateStyle: "short",
  timeStyle: "short",
});

export function formatAtendimentoDate(value: string | null): string | null {
  if (!value) return null;
  return atendimentoDateTimeFormatter.format(new Date(value));
}

export function isAtendimentoOverdue(card: AtendimentoCard): boolean {
  return (
    card.isOverdue && card.column !== "concluido" && card.column !== "cancelado"
  );
}
