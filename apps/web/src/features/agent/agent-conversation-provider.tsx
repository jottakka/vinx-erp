import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { useSession } from "../auth/session-provider";
import {
  type AgentRunFailureKind,
  type AgentRunSendResult,
  createAgentRunClient,
  isKnownAgentReasonCode,
} from "./agent-client";
import type { AgentMessage, AgentStatus } from "./agent-contract";

/**
 * Conecta o workspace do agente ao POST /agent/runs real. Cada envio é uma
 * consulta independente (o runner não mantém turnos anteriores) contra a
 * primeira empresa do bootstrap; a conversa é abortada e limpa quando a
 * empresa muda. Nenhum resultado tardio sobrevive a reset/unmount/troca.
 */
export function agentReplyText(result: AgentRunSendResult): string {
  if (!result.ok) {
    return failureText(result.failure.kind, result.failure.code);
  }
  const { status, reasonCode, text } = result.run;
  switch (status) {
    case "result":
      return text ?? withReason("Resposta concluída sem conteúdo.", reasonCode);
    case "needs_clarification":
      return (
        text ??
        withReason("O agente pede esclarecimento para continuar.", reasonCode)
      );
    case "denied":
      return (
        text ?? withReason("A execução foi negada por política.", reasonCode)
      );
    case "requires_confirmation":
      // Estado opaco v1: nenhuma ação, proposta ou botão de confirmação.
      return "A ação proposta exige confirmação explícita. Nenhuma ação foi executada.";
    case "validation_error":
      return withReason("A mensagem foi rejeitada pela validação.", reasonCode);
    case "conflict":
      return withReason(
        "Conflito de execução: a requisição já foi processada com outro conteúdo.",
        reasonCode,
      );
  }
}

function failureText(kind: AgentRunFailureKind, code?: string): string {
  switch (kind) {
    case "antiforgery":
      return "Sessão inválida para envio. Autentique-se novamente.";
    case "unauthorized":
      return "Sessão expirada. Autentique-se novamente para conversar com o agente.";
    case "rate_limited":
      return "Muitas execuções em sequência. Aguarde antes de enviar novamente.";
    case "unavailable":
      // O código do envelope 503/504 já é allow-listed pelo schema do client.
      return code
        ? `O agente está indisponível agora. (${code})`
        : "O agente está indisponível agora.";
    case "timeout":
      return "A execução do agente excedeu o tempo limite.";
    case "network":
      return "Falha de conexão com o agente.";
    case "aborted":
      return "O envio foi cancelado.";
    case "protocol":
      return "Resposta inesperada do agente.";
  }
}

function withReason(text: string, reasonCode: string | null): string {
  return isKnownAgentReasonCode(reasonCode) ? `${text} (${reasonCode})` : text;
}

function replySources(result: AgentRunSendResult) {
  return result.ok && result.run.sources
    ? result.run.sources.map((source) => ({ label: source.reference }))
    : undefined;
}

type AgentConversationContextValue = {
  closeDock: () => void;
  companyName: string | null;
  dockOpen: boolean;
  messages: readonly AgentMessage[];
  openDock: () => void;
  resetConversation: () => void;
  sendMessage: (text: string) => void;
  status: AgentStatus;
};

/**
 * Valor inerte para quando o workspace do agente não está ativo (build de
 * produção): nenhum dado de demonstração é exposto e os controles não operam.
 */
const inertValue: AgentConversationContextValue = {
  closeDock: () => {},
  companyName: null,
  dockOpen: false,
  messages: [],
  openDock: () => {},
  resetConversation: () => {},
  sendMessage: () => {},
  status: "idle",
};

const AgentConversationContext =
  createContext<AgentConversationContextValue>(inertValue);

export function isAgentWorkspaceEnabled() {
  return import.meta.env.DEV;
}

export function AgentConversationProvider({
  children,
}: {
  children: ReactNode;
}) {
  const { bootstrap } = useSession();
  const company = bootstrap?.companies[0] ?? null;
  const companyId = company?.companyId ?? null;

  const [dockOpen, setDockOpen] = useState(false);
  const [messages, setMessages] = useState<readonly AgentMessage[]>([]);
  const [status, setStatus] = useState<AgentStatus>("idle");
  const sequence = useRef(0);
  const epoch = useRef(0);
  const inFlight = useRef<AbortController | null>(null);
  const client = useRef(createAgentRunClient());

  const cancelInFlight = useCallback(() => {
    epoch.current += 1;
    inFlight.current?.abort();
    inFlight.current = null;
  }, []);

  useEffect(() => cancelInFlight, [cancelInFlight]);

  const resetConversation = useCallback(() => {
    cancelInFlight();
    setMessages([]);
    setStatus("idle");
  }, [cancelInFlight]);

  const companyRef = useRef(companyId);
  useEffect(() => {
    if (companyRef.current !== companyId) {
      companyRef.current = companyId;
      resetConversation();
    }
  }, [companyId, resetConversation]);

  const openDock = useCallback(() => setDockOpen(true), []);
  const closeDock = useCallback(() => setDockOpen(false), []);

  const sendMessage = useCallback(
    (text: string) => {
      const normalized = text.trim();

      // Trava síncrona: um envio por vez, exatamente um requestId novo.
      if (!normalized || inFlight.current) {
        return;
      }

      const controller = new AbortController();
      inFlight.current = controller;
      const runEpoch = epoch.current;

      sequence.current += 1;
      setMessages((previous) => [
        ...previous,
        {
          id: `agent-message-${sequence.current}`,
          role: "user",
          text: normalized,
        },
      ]);
      setStatus("thinking");

      client.current
        .send({
          message: normalized,
          ...(companyId ? { companyId } : {}),
          signal: controller.signal,
        })
        .then((result) => {
          if (runEpoch !== epoch.current) {
            return;
          }
          sequence.current += 1;
          setMessages((previous) => [
            ...previous,
            {
              id: `agent-message-${sequence.current}`,
              role: "agent",
              sources: replySources(result),
              text: agentReplyText(result),
            },
          ]);
        })
        // Defesa: o client contrato não lançar, mas nenhuma rejeição pode
        // ficar sem tratamento (mensagem fixa, sem eco de erro).
        .catch(() => {
          if (runEpoch !== epoch.current) {
            return;
          }
          sequence.current += 1;
          setMessages((previous) => [
            ...previous,
            {
              id: `agent-message-${sequence.current}`,
              role: "agent",
              text: "Resposta inesperada do agente.",
            },
          ]);
        })
        .finally(() => {
          if (runEpoch === epoch.current) {
            inFlight.current = null;
            setStatus("idle");
          }
        });
    },
    [companyId],
  );

  const value = useMemo(
    () => ({
      closeDock,
      companyName: company?.companyName ?? null,
      dockOpen,
      messages,
      openDock,
      resetConversation,
      sendMessage,
      status,
    }),
    [
      closeDock,
      company,
      dockOpen,
      messages,
      openDock,
      resetConversation,
      sendMessage,
      status,
    ],
  );

  return (
    <AgentConversationContext.Provider value={value}>
      {children}
    </AgentConversationContext.Provider>
  );
}

export function useAgentConversation(): AgentConversationContextValue {
  return useContext(AgentConversationContext);
}

/**
 * Envolve o shell com a conversa do agente somente em desenvolvimento.
 * Em produção devolve as children intactas, sem estado de demonstração.
 */
export function AgentWorkspace({ children }: { children: ReactNode }) {
  if (!isAgentWorkspaceEnabled()) {
    return <>{children}</>;
  }

  return <AgentConversationProvider>{children}</AgentConversationProvider>;
}
