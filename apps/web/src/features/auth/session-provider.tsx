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
  signIn: (profileId: string) => Promise<boolean>;
  signOut: () => Promise<void>;
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

  async function signOut(): Promise<void> {
    if (actionPending.current) {
      return;
    }

    actionPending.current = true;
    setError(null);
    setStatus("pending");

    try {
      await sessionGateway.logout();
      setBootstrap(null);
      setStatus("anonymous");
    } catch (reason) {
      setError(errorMessage(reason));
      setStatus(bootstrap ? "authenticated" : "anonymous");
    } finally {
      actionPending.current = false;
    }
  }

  return (
    <SessionContext.Provider
      value={{ bootstrap, error, signIn, signOut, status }}
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
