import type { QueryClient } from "@tanstack/react-query";
import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";
import type {
  FoundationBootstrapGateway,
  FoundationBootstrapLoadResult,
} from "./foundation-bootstrap-client";
import { createMockSessionGateway } from "./mock-session-gateway";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";

const genericBootstrapErrorMessage =
  "Não foi possível carregar a sessão. Recarregue a página ou entre novamente.";

/**
 * O gateway mock produz apenas mensagens first-party; o bootstrap real usa
 * resultados tipados e nunca passa a exceção bruta para a interface.
 */
function safeErrorMessage(reason: unknown): string {
  return reason instanceof Error
    ? reason.message
    : genericBootstrapErrorMessage;
}

export type SessionStatus =
  | "anonymous"
  | "authenticated"
  | "pending"
  | "unavailable";

type SessionContextValue = {
  bootstrap: BootstrapSnapshot | null;
  error: string | null;
  isSigningOut: boolean;
  refreshBootstrap: () => Promise<boolean>;
  signIn: (profileId: string) => Promise<boolean>;
  signOut: () => Promise<boolean>;
  status: SessionStatus;
};

type CacheClient = Pick<QueryClient, "clear">;

type SessionProviderProps =
  | {
      children: ReactNode;
      bootstrapClient: FoundationBootstrapGateway;
      gateway?: MockSessionGateway;
      queryClient: CacheClient;
    }
  | {
      children: ReactNode;
      bootstrapClient?: undefined;
      gateway?: MockSessionGateway;
      queryClient?: CacheClient;
    };

const missingBootstrapCacheMessage =
  "Bootstrap real exige QueryClient para limpar cache autenticado.";

/*
 * O mock pode existir sem cache de domínio. Já o bootstrap real não pode
 * publicar autorização sem a mesma instância que limpa queries autenticadas.
 */
function requireBootstrapCache(
  bootstrapClient: FoundationBootstrapGateway | undefined,
  queryClient: CacheClient | undefined,
) {
  if (bootstrapClient && !queryClient) {
    throw new Error(missingBootstrapCacheMessage);
  }
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({
  children,
  bootstrapClient,
  gateway,
  queryClient,
}: SessionProviderProps) {
  requireBootstrapCache(bootstrapClient, queryClient);
  const [sessionGateway] = useState(
    () => gateway ?? createMockSessionGateway(),
  );
  const [bootstrapGateway] = useState(() => bootstrapClient ?? null);
  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isSigningOut, setIsSigningOut] = useState(false);
  const [status, setStatus] = useState<SessionStatus>("pending");
  const actionPending = useRef(false);
  const bootstrapGeneration = useRef(0);
  const bootstrapRequest = useRef<AbortController | null>(null);

  const loadBootstrap = useCallback(
    async (signal?: AbortSignal): Promise<FoundationBootstrapLoadResult> => {
      if (bootstrapGateway) {
        try {
          return await bootstrapGateway.load(signal);
        } catch {
          return signal?.aborted
            ? { ok: false, error: { kind: "aborted" } }
            : { ok: false, error: { kind: "unavailable" } };
        }
      }

      try {
        const snapshot = await sessionGateway.getBootstrap();
        return snapshot
          ? { ok: true, snapshot }
          : { ok: false, error: { kind: "anonymous" } };
      } catch {
        return { ok: false, error: { kind: "unavailable" } };
      }
    },
    [bootstrapGateway, sessionGateway],
  );

  const supersedeBootstrap = useCallback(
    (clearCache: boolean) => {
      bootstrapGeneration.current += 1;
      bootstrapRequest.current?.abort();
      bootstrapRequest.current = null;
      if (clearCache) {
        queryClient?.clear();
      }
    },
    [queryClient],
  );

  const refreshBootstrap = useCallback(async (): Promise<boolean> => {
    if (actionPending.current) {
      return false;
    }

    supersedeBootstrap(true);
    const generation = bootstrapGeneration.current;
    const controller = new AbortController();
    bootstrapRequest.current = controller;
    setBootstrap(null);
    setError(null);
    setStatus("pending");

    const result = await loadBootstrap(controller.signal);
    if (
      generation !== bootstrapGeneration.current ||
      controller.signal.aborted
    ) {
      return false;
    }

    bootstrapRequest.current = null;
    if (result.ok) {
      setBootstrap(result.snapshot);
      setStatus("authenticated");
      return true;
    }

    if (result.error.kind === "anonymous") {
      setBootstrap(null);
      setStatus("anonymous");
      return false;
    }

    setBootstrap(null);
    setError("Não foi possível validar sua sessão. Tente novamente.");
    setStatus("unavailable");
    return false;
  }, [loadBootstrap, supersedeBootstrap]);

  useEffect(() => {
    void refreshBootstrap();
    const onOnline = () => {
      void refreshBootstrap();
    };

    window.addEventListener("online", onOnline);

    return () => {
      window.removeEventListener("online", onOnline);
      supersedeBootstrap(false);
    };
  }, [refreshBootstrap, supersedeBootstrap]);

  async function signIn(profileId: string): Promise<boolean> {
    if (actionPending.current) {
      return false;
    }

    actionPending.current = true;
    supersedeBootstrap(true);
    setError(null);
    setBootstrap(null);
    setStatus("pending");

    try {
      const snapshot = await sessionGateway.login(profileId);
      setBootstrap(snapshot);
      setStatus("authenticated");
      return true;
    } catch (reason) {
      setBootstrap(null);
      setError(safeErrorMessage(reason));
      setStatus("anonymous");
      return false;
    } finally {
      actionPending.current = false;
    }
  }

  async function signOut(): Promise<boolean> {
    if (actionPending.current) {
      return false;
    }

    actionPending.current = true;
    supersedeBootstrap(true);
    setError(null);
    setIsSigningOut(true);

    try {
      await sessionGateway.logout();
      setBootstrap(null);
      setStatus("anonymous");
      return true;
    } catch {
      setError("Não foi possível sair do aplicativo. Tente novamente.");
      setStatus(bootstrap ? "authenticated" : "anonymous");
      return false;
    } finally {
      actionPending.current = false;
      setIsSigningOut(false);
    }
  }

  return (
    <SessionContext.Provider
      value={{
        bootstrap,
        error,
        isSigningOut,
        refreshBootstrap,
        signIn,
        signOut,
        status,
      }}
    >
      {children}
    </SessionContext.Provider>
  );
}

export function useSession(): SessionContextValue {
  const session = useContext(SessionContext);

  if (!session) {
    throw new Error("useSession deve ser usado dentro de SessionProvider");
  }

  return session;
}
