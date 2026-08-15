import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { FeedbackProvider } from "../../shared/ui/feedback-provider";
import { createMockSessionGateway } from "../auth/mock-session-gateway";
import type { MockSessionGateway } from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import { AgentConversationProvider } from "./agent-conversation-provider";
import { AgentDock } from "./agent-dock";
import { AgentTrigger } from "./agent-trigger";

function Home() {
  return (
    <main>
      <h1>Início</h1>
      <AgentTrigger />
      <AgentDock />
    </main>
  );
}

async function renderAgentWorkspace(
  route = "/",
): Promise<{ router: ReturnType<typeof createMemoryRouter> }> {
  const gateway: MockSessionGateway = createMockSessionGateway();
  await gateway.login("maria");
  const router = createMemoryRouter(
    [
      { element: <Home />, path: "/" },
      {
        lazy: async () => {
          const module = await import("./agent-page");
          return { Component: module.AgentPage };
        },
        path: "/agente",
      },
    ],
    { initialEntries: [route] },
  );

  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <AgentConversationProvider>
          <FeedbackProvider>
            <RouterProvider router={router} />
          </FeedbackProvider>
        </AgentConversationProvider>
      </SessionProvider>
    </VinxThemeProvider>,
  );

  return { router };
}

async function sendDemoMessage(user: ReturnType<typeof userEvent.setup>) {
  await user.click(
    screen.getByRole("button", { name: "Abrir painel do agente" }),
  );
  const dock = within(
    screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
  );
  await user.type(
    dock.getByLabelText("Mensagem para o agente"),
    "Preciso antecipar um pagamento",
  );
  await user.click(dock.getByRole("button", { name: "Enviar" }));
  return dock;
}

describe("agent workspace", () => {
  it("answers locally with sources and a non-mutating proposal", async () => {
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await sendDemoMessage(user);

    expect(
      await dock.findByText("Preciso antecipar um pagamento"),
    ).toBeVisible();
    expect(await dock.findByText(/fontes locais/i)).toBeVisible();
    const approve = dock.getByRole("button", { name: "Aprovar execução" });

    await user.click(approve);
    expect(
      await screen.findByText(
        "Ação demonstrativa: nenhuma mutação foi executada.",
      ),
    ).toBeVisible();
  });

  it("shares the conversation between the dock and the dedicated page", async () => {
    const user = userEvent.setup();
    const { router } = await renderAgentWorkspace();
    await sendDemoMessage(user);
    expect(
      (await screen.findAllByText(/fontes locais/i)).length,
    ).toBeGreaterThan(0);

    const expandButtons = screen.getAllByRole("button", {
      name: "Abrir página dedicada do agente",
    });
    await user.click(expandButtons[expandButtons.length - 1]);
    await waitFor(() => expect(router.state.location.pathname).toBe("/agente"));
    expect(
      await screen.findByRole("heading", { name: "Agente" }),
    ).toBeVisible();
    expect(
      screen.queryAllByRole("complementary", { name: "Painel do agente" }),
    ).toHaveLength(0);
    expect(
      screen.getAllByText("Preciso antecipar um pagamento").length,
    ).toBeGreaterThan(0);

    await user.click(
      screen.getByRole("button", { name: "Voltar ao painel do agente" }),
    );
    expect(router.state.location.pathname).toBe("/");
    expect(
      screen.getAllByRole("complementary", { name: "Painel do agente" }).length,
    ).toBeGreaterThan(0);
    expect(
      screen.getAllByText("Preciso antecipar um pagamento").length,
    ).toBeGreaterThan(0);
  });
});
