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
  BrowserSessionCredentials,
  BrowserSessionOperationResult,
  BrowserSessionSignOutResult,
} from "./browser-session-client";
import type {
  FoundationBootstrapGateway,
  FoundationBootstrapLoadResult,
} from "./foundation-bootstrap-client";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";

const bootstrapErrorMessage =
  "Não foi possível validar sua sessão. Tente novamente.";
const signInErrorMessage =
  "Não foi possível entrar. Verifique suas credenciais e tente novamente.";
const signOutErrorMessage =
  "Não foi possível concluir a saída. Tente sair novamente.";
const mockSignOutErrorMessage =
  "Não foi possível sair do aplicativo. Tente novamente.";

export type SessionStatus =
  | "anonymous"
  | "authenticated"
  | "pending"
  | "unavailable"
  | "logout_recovery";
export type SessionCredentials = BrowserSessionCredentials;

type SessionContextValue = {
  bootstrap: BootstrapSnapshot | null;
  error: string | null;
  isSigningOut: boolean;
  refreshBootstrap: () => Promise<boolean>;
  signIn: (credentials: SessionCredentials) => Promise<boolean>;
  signOut: () => Promise<boolean>;
  status: SessionStatus;
};

type CacheClient = Pick<QueryClient, "clear">;
type BrowserSessionGateway = {
  signIn(
    credentials: BrowserSessionCredentials,
    signal?: AbortSignal,
  ): Promise<BrowserSessionOperationResult>;
  signOut(signal?: AbortSignal): Promise<BrowserSessionSignOutResult>;
};
type RealSessionProviderProps = {
  children: ReactNode;
  bootstrapClient: FoundationBootstrapGateway;
  browserSessionClient: BrowserSessionGateway;
  queryClient: CacheClient;
  gateway?: never;
};
type MockSessionProviderProps = {
  children: ReactNode;
  gateway: MockSessionGateway;
  bootstrapClient?: FoundationBootstrapGateway;
  browserSessionClient?: never;
  queryClient?: CacheClient;
};
type SessionProviderProps = RealSessionProviderProps | MockSessionProviderProps;

const missingSessionConfigurationMessage =
  "SessionProvider exige clientes reais ou um gateway mock explícito.";
const SessionContext = createContext<SessionContextValue | null>(null);

function isAcceptedSignOut(result: BrowserSessionSignOutResult): boolean {
  return (
    result.ok ||
    (!result.ok &&
      result.stage === "session" &&
      (result.error.status === 401 ||
        (result.error.status === 503 &&
          result.error.code === "identity_gate_unavailable")))
  );
}

export function SessionProvider({
  children,
  bootstrapClient,
  browserSessionClient,
  gateway,
  queryClient,
}: SessionProviderProps) {
  const realSession = browserSessionClient !== undefined;
  if (!realSession && !gateway)
    throw new Error(missingSessionConfigurationMessage);
  if (bootstrapClient && !queryClient) {
    throw new Error(
      "Bootstrap real exige QueryClient para limpar cache autenticado.",
    );
  }

  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isSigningOut, setIsSigningOut] = useState(false);
  const [status, setStatus] = useState<SessionStatus>("pending");
  // Espelho do bootstrap para callbacks lerem o snapshot vigente sem depender
  // do estado na closure (mudar a identidade do refreshBootstrap reexecutaria
  // o efeito de montagem em loop).
  const bootstrapRef = useRef<BootstrapSnapshot | null>(null);
  const actionPending = useRef(false);
  const generation = useRef(0);
  const bootstrapRequest = useRef<AbortController | null>(null);
  const mutationRequest = useRef<AbortController | null>(null);
  const logoutRecovery = useRef(false);

  const supersede = useCallback(
    (clearCache: boolean) => {
      generation.current += 1;
      bootstrapRequest.current?.abort();
      mutationRequest.current?.abort();
      bootstrapRequest.current = null;
      mutationRequest.current = null;
      if (clearCache) queryClient?.clear();
    },
    [queryClient],
  );

  const publishBootstrap = useCallback((snapshot: BootstrapSnapshot | null) => {
    bootstrapRef.current = snapshot;
    setBootstrap(snapshot);
  }, []);

  const loadBootstrap = useCallback(
    async (
      requestGeneration: number,
    ): Promise<FoundationBootstrapLoadResult> => {
      const controller = new AbortController();
      bootstrapRequest.current = controller;
      try {
        if (bootstrapClient)
          return await bootstrapClient.load(controller.signal);
        if (!gateway) return { ok: false, error: { kind: "unavailable" } };
        const snapshot = await gateway.getBootstrap();
        return snapshot
          ? { ok: true, snapshot }
          : { ok: false, error: { kind: "anonymous" } };
      } catch {
        return controller.signal.aborted
          ? { ok: false, error: { kind: "aborted" } }
          : { ok: false, error: { kind: "unavailable" } };
      } finally {
        if (requestGeneration === generation.current)
          bootstrapRequest.current = null;
      }
    },
    [bootstrapClient, gateway],
  );

  const commitBootstrap = useCallback(
    (
      result: FoundationBootstrapLoadResult,
      requestGeneration: number,
    ): boolean => {
      if (requestGeneration !== generation.current) return false;
      if (result.ok) {
        publishBootstrap(result.snapshot);
        setError(null);
        setStatus("authenticated");
        return true;
      }
      publishBootstrap(null);
      if (result.error.kind === "anonymous") {
        setError(null);
        setStatus("anonymous");
        return false;
      }
      if (result.error.kind !== "aborted") {
        setError(bootstrapErrorMessage);
        setStatus("unavailable");
      }
      return false;
    },
    [publishBootstrap],
  );

  const refreshBootstrap = useCallback(async (): Promise<boolean> => {
    if (actionPending.current || logoutRecovery.current) return false;
    supersede(true);
    const requestGeneration = generation.current;
    // Com um snapshot autenticado em mãos, o refresh mantém o snapshot atual
    // visível e o troca atomicamente quando a resposta chega: a janela nula
    // anterior fechava diálogos digitados à mão e remontava views no meio de
    // ações (listener 'online' e refreshes programáticos das páginas).
    const keepVisibleSnapshot = bootstrapRef.current !== null;
    if (!keepVisibleSnapshot) {
      setError(null);
      setStatus("pending");
    }
    const result = await loadBootstrap(requestGeneration);
    if (
      keepVisibleSnapshot &&
      requestGeneration === generation.current &&
      !result.ok &&
      result.error.kind !== "anonymous"
    ) {
      // Falha transitória não derruba a sessão visível; apenas sessão
      // expirada (anonymous/401) limpa o snapshot para anonymous.
      if (result.error.kind !== "aborted") setError(bootstrapErrorMessage);
      return false;
    }
    return commitBootstrap(result, requestGeneration);
  }, [commitBootstrap, loadBootstrap, supersede]);

  useEffect(() => {
    void refreshBootstrap();
    const onOnline = () => {
      if (!logoutRecovery.current) void refreshBootstrap();
    };
    window.addEventListener("online", onOnline);
    return () => {
      window.removeEventListener("online", onOnline);
      supersede(false);
    };
  }, [refreshBootstrap, supersede]);

  const signIn = useCallback(
    async (credentials: SessionCredentials): Promise<boolean> => {
      if (actionPending.current) return false;
      actionPending.current = true;
      logoutRecovery.current = false;
      supersede(true);
      const requestGeneration = generation.current;
      const controller = new AbortController();
      mutationRequest.current = controller;
      publishBootstrap(null);
      setError(null);
      setStatus("pending");
      try {
        if (!browserSessionClient) {
          if (!gateway) return false;
          const snapshot = await gateway.login(credentials.email.trim());
          if (
            controller.signal.aborted ||
            requestGeneration !== generation.current
          )
            return false;
          publishBootstrap(snapshot);
          setStatus("authenticated");
          return true;
        }
        const result = await browserSessionClient.signIn(
          { email: credentials.email.trim(), password: credentials.password },
          controller.signal,
        );
        if (
          controller.signal.aborted ||
          requestGeneration !== generation.current
        )
          return false;
        if (!result.ok) {
          setError(signInErrorMessage);
          setStatus("anonymous");
          return false;
        }
        return commitBootstrap(
          await loadBootstrap(requestGeneration),
          requestGeneration,
        );
      } catch {
        if (
          !controller.signal.aborted &&
          requestGeneration === generation.current
        ) {
          publishBootstrap(null);
          setError(signInErrorMessage);
          setStatus("anonymous");
        }
        return false;
      } finally {
        if (requestGeneration === generation.current) {
          mutationRequest.current = null;
          actionPending.current = false;
        }
      }
    },
    [
      browserSessionClient,
      commitBootstrap,
      gateway,
      loadBootstrap,
      publishBootstrap,
      supersede,
    ],
  );

  const signOut = useCallback(async (): Promise<boolean> => {
    if (actionPending.current) return false;
    actionPending.current = true;
    supersede(true);
    const requestGeneration = generation.current;
    const controller = new AbortController();
    mutationRequest.current = controller;
    publishBootstrap(null);
    setError(null);
    setIsSigningOut(true);
    setStatus("pending");
    try {
      if (!browserSessionClient) {
        if (!gateway) return false;
        await gateway.logout();
        if (
          controller.signal.aborted ||
          requestGeneration !== generation.current
        )
          return false;
        setStatus("anonymous");
        return true;
      }
      const result = await browserSessionClient.signOut(controller.signal);
      if (controller.signal.aborted || requestGeneration !== generation.current)
        return false;
      if (isAcceptedSignOut(result)) {
        logoutRecovery.current = false;
        setStatus("anonymous");
        return true;
      }
      logoutRecovery.current = true;
      setError(signOutErrorMessage);
      setStatus("logout_recovery");
      return false;
    } catch {
      if (
        !controller.signal.aborted &&
        requestGeneration === generation.current
      ) {
        logoutRecovery.current = true;
        setError(
          browserSessionClient ? signOutErrorMessage : mockSignOutErrorMessage,
        );
        setStatus("logout_recovery");
      }
      return false;
    } finally {
      if (requestGeneration === generation.current) {
        mutationRequest.current = null;
        actionPending.current = false;
        setIsSigningOut(false);
      }
    }
  }, [browserSessionClient, gateway, supersede]);

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
  if (!session)
    throw new Error("useSession deve ser usado dentro de SessionProvider");
  return session;
}
