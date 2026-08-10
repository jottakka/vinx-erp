import { createBrowserRouter, Navigate } from "react-router-dom";
import { LoginPage } from "../features/auth/login-page";
import { ProtectedRoute } from "./protected-route";
import { PrototypeEntryPage } from "./prototype-entry-page";

export function createAppRouter() {
  return createBrowserRouter([
    {
      element: <Navigate replace to="/login" />,
      path: "/",
    },
    {
      element: <LoginPage />,
      path: "/login",
    },
    {
      element: (
        <ProtectedRoute>
          <PrototypeEntryPage />
        </ProtectedRoute>
      ),
      path: "/protected",
    },
    {
      element: <Navigate replace to="/login" />,
      path: "*",
    },
  ]);
}
