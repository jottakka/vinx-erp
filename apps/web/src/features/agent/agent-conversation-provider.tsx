import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { hasCompanyCapability } from "../auth/company-capabilities";
import type { AuthorizedCompanyMembership } from "../auth/session-contract";
import { useSession } from "../auth/session-provider";
import {
  type AgentAttachmentUploadStage,
  createAgentAttachmentClient,
} from "./agent-attachments-client";
import {
  type AgentRunFailureKind,
  type AgentRunReasonCode,
  type AgentRunSendResult,
  createAgentRunClient,
} from "./agent-client";
import type { AgentMessage, AgentStatus } from "./agent-contract";

export function agentReplyText(result: AgentRunSendResult): string {
  if (!result.ok) {
    return failureText(result.failure.kind);
  }
  const { status, reasonCode, text } = result.run;
  switch (status) {
    case "result":
      return text ?? "Resposta concluída sem conteúdo.";
    case "needs_clarification":
      if (reasonCode === "document_context_unavailable") {
        return "Não foi possível usar o anexo. Remova-o ou verifique-o antes de tentar novamente.";
      }
      return text ?? "O agente pede esclarecimento para continuar.";
    case "denied":
      return text ?? deniedText(reasonCode);
    case "requires_confirmation":
      // Estado opaco v1: nenhuma ação, proposta ou botão de confirmação.
      return "A ação proposta exige confirmação explícita. Nenhuma ação foi executada.";
    case "validation_error":
      return "A mensagem foi rejeitada pela validação.";
    case "conflict":
      return "Conflito de execução: a requisição já foi processada com outro conteúdo.";
  }
}

const companyRequiredMessage = "Selecione uma empresa para usar o assistente.";
const capabilityDeniedMessage =
  "O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.";

function deniedText(reasonCode: AgentRunReasonCode | null): string {
  switch (reasonCode) {
    case "capability_denied":
      return capabilityDeniedMessage;
    case "company_required":
      return companyRequiredMessage;
    case "company_not_authorized":
      return "Você não tem acesso ao assistente nesta empresa. Selecione outra empresa ou solicite acesso ao administrador.";
    default:
      return "Não foi possível executar esta solicitação por uma regra de acesso.";
  }
}

function failureText(kind: AgentRunFailureKind): string {
  switch (kind) {
    case "antiforgery":
      return "Sessão inválida para envio. Autentique-se novamente.";
    case "unauthorized":
      return "Sessão expirada. Autentique-se novamente para conversar com o agente.";
    case "rate_limited":
      return "Muitas execuções em sequência. Aguarde antes de enviar novamente.";
    case "unavailable":
      return "O assistente está temporariamente indisponível. Tente novamente mais tarde.";
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

function replySources(result: AgentRunSendResult) {
  return result.ok && result.run.sources
    ? result.run.sources.map((source) => ({ label: source.reference }))
    : undefined;
}

type AgentConversationContextValue = {
  access: AgentAccess;
  authorizedCompanies: readonly AuthorizedCompanyMembership[];
  attachments: readonly AgentConversationAttachment[];
  attachmentSelectionAvailable: boolean;
  attachmentSelectionLimitReached: boolean;
  closeDock: () => void;
  companyName: string | null;
  selectCompany: (companyId: string) => void;
  selectedCompanyId: string;
  dockOpen: boolean;
  messages: readonly AgentMessage[];
  openDock: () => void;
  removeAttachment: (localId: string) => void;
  resetConversation: () => void;
  selectAttachments: (files: readonly File[]) => void;
  sendMessage: (text: string) => void;
  status: AgentStatus;
};

export type AgentAccess =
  | { available: true; message: null }
  | { available: false; message: string };

function resolveAgentAccess(
  company: AuthorizedCompanyMembership | null,
): AgentAccess {
  if (!company) return { available: false, message: companyRequiredMessage };
  return hasCompanyCapability(company, "ai.read")
    ? { available: true, message: null }
    : { available: false, message: capabilityDeniedMessage };
}

export type AgentConversationAttachment = {
  errorStage?: AgentAttachmentUploadStage;
  fileId?: string;
  fileName: string;
  localId: string;
  mimeType: string;
  sizeBytes: number;
  state: "uploading" | "ready" | "failed";
};

/**
 * Valor inerte para quando o workspace do agente não está ativo (build de
 * produção): nenhum dado de demonstração é exposto e os controles não operam.
 */
const inertValue: AgentConversationContextValue = {
  access: { available: false, message: companyRequiredMessage },
  authorizedCompanies: [],
  attachments: [],
  attachmentSelectionAvailable: false,
  attachmentSelectionLimitReached: false,
  closeDock: () => {},
  companyName: null,
  selectCompany: () => {},
  selectedCompanyId: "",
  dockOpen: false,
  messages: [],
  openDock: () => {},
  removeAttachment: () => {},
  resetConversation: () => {},
  selectAttachments: () => {},
  sendMessage: () => {},
  status: "idle",
};

const AgentConversationContext =
  createContext<AgentConversationContextValue>(inertValue);

export function createAgentConversationScopeKey({
  companyId,
  membershipId,
  userId,
}: {
  companyId: string | null;
  membershipId: string | null;
  userId: string | null;
}) {
  return JSON.stringify([userId, membershipId, companyId]);
}

export function isAgentWorkspaceEnabled() {
  return import.meta.env.DEV;
}

/**
 * Conecta o workspace do agente ao POST /agent/runs para a empresa escolhida.
 * A conversa é cancelada e limpa quando a empresa, o usuário ou o vínculo muda.
 * Resultados tardios não são exibidos após reset, desmontagem ou troca.
 */
export function AgentConversationProvider({
  children,
}: {
  children: ReactNode;
}) {
  const { bootstrap } = useSession();
  const authorizedCompanies = bootstrap?.companies ?? [];
  const [selection, setSelection] = useState<{
    companyId: string;
    membershipId: string;
    userId: string;
  } | null>(null);
  const selectedCompany =
    selection && bootstrap?.user.id === selection.userId
      ? (authorizedCompanies.find(
          (company) =>
            company.companyId === selection.companyId &&
            company.membershipId === selection.membershipId,
        ) ?? null)
      : null;
  const selectCompany = useCallback(
    (companyId: string) => {
      const company = authorizedCompanies.find(
        (candidate) => candidate.companyId === companyId,
      );
      setSelection(
        company && bootstrap
          ? {
              companyId: company.companyId,
              membershipId: company.membershipId,
              userId: bootstrap.user.id,
            }
          : null,
      );
    },
    [authorizedCompanies, bootstrap],
  );
  useLayoutEffect(() => {
    if (selection && !selectedCompany) setSelection(null);
  }, [selectedCompany, selection]);
  const [dockOpen, setDockOpen] = useState(false);
  const openDock = useCallback(() => setDockOpen(true), []);
  const closeDock = useCallback(() => setDockOpen(false), []);
  const scopeKey = createAgentConversationScopeKey({
    companyId: selectedCompany?.companyId ?? null,
    membershipId: selectedCompany?.membershipId ?? null,
    userId: bootstrap?.user.id ?? null,
  });

  return (
    <ScopedAgentConversationProvider
      closeDock={closeDock}
      company={selectedCompany}
      dockOpen={dockOpen}
      key={scopeKey}
      openDock={openDock}
      authorizedCompanies={authorizedCompanies}
      onCompanyChange={selectCompany}
    >
      {children}
    </ScopedAgentConversationProvider>
  );
}

function ScopedAgentConversationProvider({
  children,
  closeDock,
  company,
  dockOpen,
  openDock,
  authorizedCompanies,
  onCompanyChange,
}: {
  children: ReactNode;
  closeDock: () => void;
  company: AuthorizedCompanyMembership | null;
  dockOpen: boolean;
  openDock: () => void;
  authorizedCompanies: readonly AuthorizedCompanyMembership[];
  onCompanyChange: (companyId: string) => void;
}) {
  const companyId = company?.companyId ?? null;
  const access = resolveAgentAccess(company);
  const attachmentSelectionAvailable = access.available;

  const [messages, setMessages] = useState<readonly AgentMessage[]>([]);
  const [attachments, setAttachments] = useState<
    readonly AgentConversationAttachment[]
  >([]);
  const [attachmentSelectionLimitReached, setAttachmentSelectionLimitReached] =
    useState(false);
  const [status, setStatus] = useState<AgentStatus>("idle");
  const sequence = useRef(0);
  const attachmentSequence = useRef(0);
  const epoch = useRef(0);
  const attachmentEpoch = useRef(0);
  const inFlight = useRef<AbortController | null>(null);
  const attachmentUploads = useRef(new Map<string, AbortController>());
  const attachmentSnapshot = useRef<readonly AgentConversationAttachment[]>([]);
  const client = useRef(createAgentRunClient());
  const attachmentClient = useRef(createAgentAttachmentClient());

  const replaceAttachments = useCallback(
    (next: readonly AgentConversationAttachment[]) => {
      attachmentSnapshot.current = next;
      setAttachments(next);
    },
    [],
  );

  const cancelInFlight = useCallback(() => {
    epoch.current += 1;
    inFlight.current?.abort();
    inFlight.current = null;
  }, []);

  const abortAttachments = useCallback(
    (clearState: boolean) => {
      attachmentEpoch.current += 1;
      for (const controller of attachmentUploads.current.values()) {
        controller.abort();
      }
      attachmentUploads.current.clear();
      if (clearState) {
        replaceAttachments([]);
        setAttachmentSelectionLimitReached(false);
      }
    },
    [replaceAttachments],
  );

  useLayoutEffect(
    () => () => {
      cancelInFlight();
      abortAttachments(false);
    },
    [abortAttachments, cancelInFlight],
  );

  const resetConversation = useCallback(() => {
    cancelInFlight();
    abortAttachments(true);
    setMessages([]);
    setStatus("idle");
  }, [abortAttachments, cancelInFlight]);

  const removeAttachment = useCallback(
    (localId: string) => {
      attachmentUploads.current.get(localId)?.abort();
      attachmentUploads.current.delete(localId);
      setAttachmentSelectionLimitReached(false);
      replaceAttachments(
        attachmentSnapshot.current.filter(
          (attachment) => attachment.localId !== localId,
        ),
      );
    },
    [replaceAttachments],
  );

  const selectAttachments = useCallback(
    (files: readonly File[]) => {
      if (!companyId || !attachmentSelectionAvailable || !files.length) {
        return;
      }
      if (attachmentSnapshot.current.length + files.length > 3) {
        setAttachmentSelectionLimitReached(true);
        return;
      }

      setAttachmentSelectionLimitReached(false);
      const selected = files.map((file) => {
        attachmentSequence.current += 1;
        return {
          fileName: file.name,
          localId: `agent-attachment-${attachmentSequence.current}`,
          mimeType: file.type,
          sizeBytes: file.size,
          state: "uploading" as const,
        };
      });
      replaceAttachments([...attachmentSnapshot.current, ...selected]);

      const selectionEpoch = attachmentEpoch.current;
      for (const [index, selection] of selected.entries()) {
        const file = files[index];
        if (!file) continue;
        const controller = new AbortController();
        attachmentUploads.current.set(selection.localId, controller);

        attachmentClient.current
          .upload({ companyId, file, signal: controller.signal })
          .then((result) => {
            if (
              selectionEpoch !== attachmentEpoch.current ||
              attachmentUploads.current.get(selection.localId) !== controller
            ) {
              return;
            }
            replaceAttachments(
              attachmentSnapshot.current.map((attachment) => {
                if (attachment.localId !== selection.localId) {
                  return attachment;
                }
                return result.ok
                  ? {
                      ...attachment,
                      fileId: result.attachment.fileId,
                      state: "ready" as const,
                    }
                  : {
                      ...attachment,
                      errorStage: result.error.stage,
                      state: "failed" as const,
                    };
              }),
            );
          })
          .catch(() => {
            if (
              selectionEpoch === attachmentEpoch.current &&
              attachmentUploads.current.get(selection.localId) === controller
            ) {
              replaceAttachments(
                attachmentSnapshot.current.map((attachment) =>
                  attachment.localId === selection.localId
                    ? {
                        ...attachment,
                        errorStage: "validation",
                        state: "failed" as const,
                      }
                    : attachment,
                ),
              );
            }
          })
          .finally(() => {
            if (
              attachmentUploads.current.get(selection.localId) === controller
            ) {
              attachmentUploads.current.delete(selection.localId);
            }
          });
      }
    },
    [attachmentSelectionAvailable, companyId, replaceAttachments],
  );

  const sendMessage = useCallback(
    (text: string) => {
      if (!access.available) {
        return;
      }
      const normalized = text.trim();
      const selectedAttachments = attachmentSnapshot.current;
      const attachmentIds = selectedAttachments.flatMap((attachment) =>
        attachment.state === "ready" && attachment.fileId
          ? [attachment.fileId]
          : [],
      );

      // Trava síncrona: um envio por vez, exatamente um requestId novo.
      if (
        !companyId ||
        !normalized ||
        inFlight.current ||
        attachmentIds.length !== selectedAttachments.length
      ) {
        return;
      }

      const controller = new AbortController();
      inFlight.current = controller;
      const runEpoch = epoch.current;

      sequence.current += 1;
      replaceAttachments([]);
      setAttachmentSelectionLimitReached(false);
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
          companyId,
          ...(attachmentIds.length ? { attachments: attachmentIds } : {}),
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
    [access.available, companyId, replaceAttachments],
  );

  const value = useMemo(
    () => ({
      access,
      authorizedCompanies,
      attachments,
      attachmentSelectionAvailable,
      attachmentSelectionLimitReached,
      closeDock,
      companyName: company?.companyName ?? null,
      selectCompany: onCompanyChange,
      selectedCompanyId: companyId ?? "",
      dockOpen,
      messages,
      openDock,
      removeAttachment,
      resetConversation,
      selectAttachments,
      sendMessage,
      status,
    }),
    [
      closeDock,
      authorizedCompanies,
      company,
      onCompanyChange,
      dockOpen,
      access,
      messages,
      openDock,
      attachmentSelectionLimitReached,
      attachmentSelectionAvailable,
      attachments,
      removeAttachment,
      resetConversation,
      selectAttachments,
      sendMessage,
      status,
      companyId,
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
