import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { createMockSessionGateway } from "../features/auth/mock-session-gateway";
import type { MockSessionGateway } from "../features/auth/session-contract";
import { SessionProvider } from "../features/auth/session-provider";
import { createAppRouter } from "./router";
import { VinxThemeProvider } from "./theme/vinx-theme-provider";

function renderRoute(path: string, gateway: MockSessionGateway) {
  window.history.pushState({}, "", path);
  const router = createAppRouter();

  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <RouterProvider router={router} />
      </SessionProvider>
    </VinxThemeProvider>,
  );

  return router;
}

async function signedInGateway() {
  const gateway = createMockSessionGateway();
  await gateway.login("maria");
  return gateway;
}

describe("application routes", () => {
  it.each([
    ["/paguai", "Paguai"],
    ["/atendimento", "Atendimento"],
    ["/kanban", "Kanban"],
    ["/pessoas", "Pessoas"],
  ])("protects and renders %s", async (path, heading) => {
    const router = renderRoute(path, await signedInGateway());

    expect(await screen.findByRole("heading", { name: heading })).toBeVisible();
    expect(router.state.location.pathname).toBe(path);
  });

  it("renders the professional all-authorized home at the authenticated root", async () => {
    const router = renderRoute("/", await signedInGateway());

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/");
  });

  it("keeps /protected as an authenticated compatibility redirect to home", async () => {
    const router = renderRoute("/protected", await signedInGateway());

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  });

  it("returns an anonymous help deep link safely after sign-in", async () => {
    const user = userEvent.setup();
    const router = renderRoute("/ajuda", createMockSessionGateway());

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByRole("heading", { name: "Ajuda" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/ajuda");
  });

  it("returns an anonymous module deep link safely after sign-in", async () => {
    const user = userEvent.setup();
    const router = renderRoute("/paguai", createMockSessionGateway());

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Paguai" }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/paguai");
  });

  it("rejects a query-bearing protected return path after sign-in", async () => {
    const user = userEvent.setup();
    const router = renderRoute(
      "/ajuda?origem=externa",
      createMockSessionGateway(),
    );

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/");
  });

  it("rejects a query-bearing module return path after sign-in", async () => {
    const user = userEvent.setup();
    const router = renderRoute(
      "/paguai?empresa=alfa",
      createMockSessionGateway(),
    );

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/");
  });

  it("returns to login after successful logout", async () => {
    const user = userEvent.setup();
    const router = renderRoute("/", await signedInGateway());

    await screen.findByRole("heading", { name: "Início" });
    await user.click(screen.getByRole("button", { name: "Sair" }));

    expect(
      await screen.findByRole("heading", {
        name: "Entrada do ambiente de desenvolvimento",
      }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe("/login");
  });

  it("returns an authenticated unknown route to home", async () => {
    const router = renderRoute(
      "/caminho-desconhecido",
      await signedInGateway(),
    );

    expect(
      await screen.findByRole("heading", { name: "Início" }),
    ).toBeVisible();
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
  });
});
