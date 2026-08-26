import type { AtendimentoPriority, AtendimentoColumn } from "./atendimento-client";

/**
 * Fonte única de rótulos e cores dos chips (status/prioridade/canal), sempre
 * com tokens do tema — nunca hex cru nos componentes.
 */
export const columnLabels: Record<AtendimentoColumn, string> = {
  concluido: "Concluído",
  em_andamento: "Em andamento",
  novo: "Novo",
};

export const priorityLabels: Record<AtendimentoPriority, string> = {
  alta: "Alta",
  baixa: "Baixa",
  critica: "Crítica",
  media: "Média",
};

/** Chip color semântico por prioridade (tokens do tema). */
export const priorityColors: Record<
  AtendimentoPriority,
  "default" | "error" | "info" | "success" | "warning"
> = {
  alta: "warning",
  baixa: "success",
  critica: "error",
  media: "info",
};

export const channelLabels: Record<string, string> = {
  email: "E-mail",
  manual: "Manual",
  web: "Web",
  whatsapp: "WhatsApp",
};

export function priorityLabel(value: string | null | undefined): string {
  if (!value) return "";
  return (
    priorityLabels[value as AtendimentoPriority] ?? value
  );
}

export function priorityColor(
  value: string | null | undefined,
): "default" | "error" | "info" | "success" | "warning" {
  if (!value) return "default";
  return priorityColors[value as AtendimentoPriority] ?? "default";
}

export function channelLabel(key: string): string {
  return channelLabels[key] ?? key;
}

export function eventLabels(kind: string): string {
  switch (kind) {
    case "assigned":
      return "Atribuído";
    case "created":
      return "Criado";
    case "fields_updated":
      return "Campos atualizados";
    case "moved":
      return "Movido";
    case "watcher_added":
      return "Observador adicionado";
    case "watcher_removed":
      return "Observador removido";
    default:
      return kind;
  }
}

/** Avatar de iniciais a partir do nome de exibição (máx. 2 letras). */
export function initialsFor(displayName: string): string {
  return displayName
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part.charAt(0).toUpperCase())
    .join("");
}
