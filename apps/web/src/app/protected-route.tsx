import type { ReactNode } from "react";
import { Navigate } from "react-router-dom";
import { useSession } from "../features/auth/session-provider";

type ProtectedRouteProps = {
  children: ReactNode;
};

export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const { bootstrap, status } = useSession();

  if (status === "pending") {
    return null;
  }

  if (!bootstrap) {
    return <Navigate replace to="/login" />;
  }

  return children;
}
