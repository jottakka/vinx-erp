import {
  createContext,
  type ReactNode,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";
import { createMockSessionGateway } from "./mock-session-gateway";
import type { BootstrapSnapshot, MockSessionGateway } from "./session-contract";

export type SessionStatus = "anonymous" | "authenticated" | "pending";

type SessionContextValue = {
  bootstrap: BootstrapSnapshot | null;
  error: string | null;
  isSigningOut: boolean;
  signIn: (profileId: string) => Promise<boolean>;
  signOut: () => Promise<boolean>;
  status: SessionStatus;
};

type SessionProviderProps = {
  children: ReactNode;
  gateway?: MockSessionGateway;
};

const SessionContext = createContext<SessionContextValue | null>(null);

function errorMessage(error: unknown): string {
  return error instanceof Error
    ? error.message
    : "Não foi possível entrar no protótipo";
}

export function SessionProvider({ children, gateway }: SessionProviderProps) {
  const [sessionGateway] = useState(
    () => gateway ?? createMockSessionGateway(),
  );
  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isSigningOut, setIsSigningOut] = useState(false);
  const [status, setStatus] = useState<SessionStatus>("pending");
  const actionPending = useRef(false);

  useEffect(() => {
    let mounted = true;

    void sessionGateway.getBootstrap().then(
      (snapshot) => {
        if (!mounted) {
          return;
        }

        setBootstrap(snapshot);
        setStatus(snapshot ? "authenticated" : "anonymous");
      },
      (reason: unknown) => {
        if (!mounted) {
          return;
        }

        setBootstrap(null);
        setError(errorMessage(reason));
        setStatus("anonymous");
      },
    );

    return () => {
      mounted = false;
    };
  }, [sessionGateway]);

  async function signIn(profileId: string): Promise<boolean> {
    if (actionPending.current) {
      return false;
    }

    actionPending.current = true;
    setError(null);
    setStatus("pending");

    try {
      const snapshot = await sessionGateway.login(profileId);
      setBootstrap(snapshot);
      setStatus("authenticated");
      return true;
    } catch (reason) {
      setBootstrap(null);
      setError(errorMessage(reason));
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
      value={{ bootstrap, error, isSigningOut, signIn, signOut, status }}
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
