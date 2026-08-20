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
  createNotificationsClient,
  type NotificationsClient,
} from "./notifications-client";

type NotificationsState = {
  unreadCount: number | null;
  refreshUnreadCount: () => Promise<void>;
};

const NotificationsContext = createContext<NotificationsState | null>(null);

export function useNotifications(): NotificationsState {
  const context = useContext(NotificationsContext);
  if (!context)
    throw new Error("useNotifications requires NotificationsProvider");
  return context;
}

/**
 * Contador de não lidas sem polling: busca no bootstrap válido, no focus da
 * janela, após marcação e ao entrar na rota. Erro nunca reduz o contador nem
 * marca item; respostas desatualizadas são descartadas por época.
 */
export function NotificationsProvider({
  children,
  client,
}: {
  children: ReactNode;
  client?: NotificationsClient;
}) {
  const { bootstrap } = useSession();
  const notificationsClient = useMemo(
    () => client ?? createNotificationsClient(),
    [client],
  );
  const [unreadCount, setUnreadCount] = useState<number | null>(null);
  const epochRef = useRef(0);
  const userId = bootstrap?.user.id ?? null;

  const refreshUnreadCount = useCallback(async () => {
    const epoch = ++epochRef.current;
    const result = await notificationsClient.fetchUnreadCount();
    if (epoch !== epochRef.current) return;
    if (result.ok) {
      setUnreadCount(result.value.unreadCount);
    }
    // Falha mantém o valor anterior (ou null) — nunca zera por erro.
  }, [notificationsClient]);

  useEffect(() => {
    // Troca de sessão/bootstrap nulo zera o estado da sessão anterior.
    epochRef.current++;
    setUnreadCount(null);
    if (!userId) return;
    void refreshUnreadCount();
  }, [userId, refreshUnreadCount]);

  useEffect(() => {
    if (!userId) return;
    function onWindowFocus() {
      void refreshUnreadCount();
    }
    window.addEventListener("focus", onWindowFocus);
    return () => window.removeEventListener("focus", onWindowFocus);
  }, [userId, refreshUnreadCount]);

  const state = useMemo<NotificationsState>(
    () => ({ unreadCount, refreshUnreadCount }),
    [unreadCount, refreshUnreadCount],
  );

  return (
    <NotificationsContext.Provider value={state}>
      {children}
    </NotificationsContext.Provider>
  );
}
