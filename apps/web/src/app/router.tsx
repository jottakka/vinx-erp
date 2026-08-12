import { createBrowserRouter, Navigate } from "react-router-dom";
import { LoginPage } from "../features/auth/login-page";
import { AppShell } from "./app-shell";
import { HelpPage } from "./help-page";
import { HomePage } from "./home-page";
import { ProtectedRoute } from "./protected-route";

export function createAppRouter() {
  return createBrowserRouter([
    {
      element: <LoginPage />,
      path: "/login",
    },
    {
      element: (
        <ProtectedRoute>
          <AppShell />
        </ProtectedRoute>
      ),
      path: "/",
      children: [
        { element: <HomePage />, index: true },
        { element: <HelpPage />, path: "ajuda" },
      ],
    },
    {
      element: (
        <ProtectedRoute>
          <Navigate replace to="/" />
        </ProtectedRoute>
      ),
      path: "/protected",
    },
    {
      element: (
        <ProtectedRoute>
          <Navigate replace to="/" />
        </ProtectedRoute>
      ),
      path: "*",
    },
  ]);
}
