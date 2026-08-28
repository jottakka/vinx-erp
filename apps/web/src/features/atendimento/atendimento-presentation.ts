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
