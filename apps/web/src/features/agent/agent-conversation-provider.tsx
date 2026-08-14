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
import type {
  AgentMessage,
  AgentProposal,
  AgentStatus,
} from "./agent-contract";

const replyDelayMs = 600;

const demoReply = "Resposta demonstrativa montada a partir de fontes locais.";
const demoSources: readonly { label: string }[] = [
  { label: "Política local de despesas" },
  { label: "Solicitação Paguai nº 123" },
];
const demoProposal: AgentProposal = {
  amount: 1250,
  id: "agent-proposal-1",
  summary:
    "Adiantar o vencimento da solicitação selecionada após a conferência do comprovante.",
  title: "Antecipar pagamento da solicitação",
};

type AgentConversationContextValue = {
  closeDock: () => void;
  dockOpen: boolean;
  messages: readonly AgentMessage[];
  openDock: () => void;
  proposal: AgentProposal | null;
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
  dockOpen: false,
  messages: [],
  openDock: () => {},
  proposal: null,
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
  const [dockOpen, setDockOpen] = useState(false);
  const [messages, setMessages] = useState<readonly AgentMessage[]>([]);
  const [proposal, setProposal] = useState<AgentProposal | null>(null);
  const [status, setStatus] = useState<AgentStatus>("idle");
  const sequence = useRef(0);
  const replyTimeout = useRef<number | null>(null);

  useEffect(
    () => () => {
      if (replyTimeout.current !== null) {
        window.clearTimeout(replyTimeout.current);
      }
    },
    [],
  );

  const openDock = useCallback(() => setDockOpen(true), []);
  const closeDock = useCallback(() => setDockOpen(false), []);

  const resetConversation = useCallback(() => {
    if (replyTimeout.current !== null) {
      window.clearTimeout(replyTimeout.current);
      replyTimeout.current = null;
    }

    setMessages([]);
    setProposal(null);
    setStatus("idle");
  }, []);

  const sendMessage = useCallback((text: string) => {
    const normalized = text.trim();

    if (!normalized || replyTimeout.current !== null) {
      return;
    }

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

    replyTimeout.current = window.setTimeout(() => {
      replyTimeout.current = null;
      sequence.current += 1;
      setMessages((previous) => [
        ...previous,
        {
          id: `agent-message-${sequence.current}`,
          role: "agent",
          sources: demoSources,
          text: demoReply,
        },
      ]);
      setProposal(demoProposal);
      setStatus("idle");
    }, replyDelayMs);
  }, []);

  const value = useMemo(
    () => ({
      closeDock,
      dockOpen,
      messages,
      openDock,
      proposal,
      resetConversation,
      sendMessage,
      status,
    }),
    [
      closeDock,
      dockOpen,
      messages,
      openDock,
      proposal,
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
