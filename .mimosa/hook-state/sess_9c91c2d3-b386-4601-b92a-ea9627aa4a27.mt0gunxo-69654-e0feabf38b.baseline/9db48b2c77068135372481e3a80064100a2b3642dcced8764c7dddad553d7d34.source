import {
  createBrowserRouter,
  Navigate,
  type RouteObject,
} from "react-router-dom";
import { LoginPage } from "../features/auth/login-page";
import {
  type AppRouteDefinition,
  appRouteManifest,
} from "./app-route-manifest";
import { AppShell } from "./app-shell";
import { ProtectedRoute } from "./protected-route";

function toChildRoute({ element, path }: AppRouteDefinition): RouteObject {
  return path === "/"
    ? { element, index: true }
    : { element, path: path.slice(1) };
}

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
      children: appRouteManifest.map(toChildRoute),
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
