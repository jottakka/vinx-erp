import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { FeedbackProvider } from "../../shared/ui/feedback-provider";
import { createMockSessionGateway } from "../auth/mock-session-gateway";
import type {
  BootstrapSnapshot,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider, useSession } from "../auth/session-provider";
import { AgentConversationProvider } from "./agent-conversation-provider";
import { AgentDock } from "./agent-dock";
import { AgentTrigger } from "./agent-trigger";

const agentRunsPath = "/agent/runs";
const alfaCompanyId = "33333333-3333-4333-8333-333333333333";
const correlationId = "0192b0c0-0000-7000-8000-000000000001";

function Home() {
  return (
    <main>
      <h1>Início</h1>
      <AgentTrigger />
      <AgentDock />
    </main>
  );
}

// Gatilho de refresh do bootstrap dentro da sessão para simular troca de
// empresa ativa enquanto uma execução está pendente.
function RefreshBootstrapButton() {
  const { refreshBootstrap } = useSession();
  return (
    <button onClick={() => void refreshBootstrap()} type="button">
      Atualizar sessão
    </button>
  );
}

async function renderAgentWorkspace(
  route = "/",
  gateway: MockSessionGateway = createMockSessionGateway(),
): Promise<{
  router: ReturnType<typeof createMemoryRouter>;
  unmount: () => void;
}> {
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

  const { unmount } = render(
    <VinxThemeProvider>
      <SessionProvider gateway={gateway}>
        <AgentConversationProvider>
          <FeedbackProvider>
            <RefreshBootstrapButton />
            <RouterProvider router={router} />
          </FeedbackProvider>
        </AgentConversationProvider>
      </SessionProvider>
    </VinxThemeProvider>,
  );

  return { router, unmount };
}

async function openDockAndType(
  user: ReturnType<typeof userEvent.setup>,
  message: string,
) {
  await user.click(
    screen.getByRole("button", { name: "Abrir painel do agente" }),
  );
  const dock = within(
    screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
  );
  await user.type(dock.getByLabelText("Mensagem para o agente"), message);
  return dock;
}

function antiforgeryTokenResponse() {
  return new Response(JSON.stringify({ requestToken: "csrf-token" }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

function agentRunResponse(
  body: Record<string, unknown>,
  status = 200,
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json",
      "X-Correlation-ID": correlationId,
    },
  });
}

function runCalls(fetchSpy: ReturnType<typeof vi.fn>) {
  return fetchSpy.mock.calls.filter(([path]) => path === agentRunsPath);
}

afterEach(() => vi.unstubAllGlobals());

describe("agent workspace", () => {
  it("sends the message to POST /agent/runs and renders the typed result with sources", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockResolvedValueOnce(
        agentRunResponse({
          status: "result",
          reasonCode: "ok",
          text: "Resposta liberada pelo gate",
          sources: [{ reference: "Solicitação Paguai nº 123" }],
          correlationId,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "Preciso antecipar um pagamento");
    expect(dock.getByText("Alfa · Consultas independentes")).toBeVisible();
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(await dock.findByText("Resposta liberada pelo gate")).toBeVisible();
    expect(dock.getByText("Solicitação Paguai nº 123")).toBeVisible();

    const calls = runCalls(fetchSpy);
    expect(calls).toHaveLength(1);
    const body = JSON.parse(calls[0][1].body as string);
    expect(body).toEqual({
      companyId: alfaCompanyId,
      journey: "read_only_summarize_v1",
      message: "Preciso antecipar um pagamento",
      requestId: expect.stringMatching(
        /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
      ),
    });
    expect(calls[0][1]).toEqual(
      expect.objectContaining({
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": "csrf-token",
        },
        method: "POST",
        mode: "same-origin",
        redirect: "error",
      }),
    );
  });

  it("renders a fixed confirmation notice without any proposal card for opaque confirmation state", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(antiforgeryTokenResponse())
        .mockResolvedValueOnce(
          agentRunResponse({
            status: "requires_confirmation",
            reasonCode: "tool_proposal_requires_confirmation",
            text: null,
            sources: null,
            correlationId,
          }),
        ),
    );
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "Aprovar execução de pagamento");
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(await dock.findByText(/exige confirmação explícita/i)).toBeVisible();
    expect(
      screen.queryByRole("button", { name: "Aprovar execução" }),
    ).toBeNull();
  });

  it("blocks a second submission while one is in flight and issues a fresh requestId per accepted send", async () => {
    let resolveRun: (value: Response) => void = () => {};
    const pendingRun = new Promise<Response>(
      (resolve) => (resolveRun = resolve),
    );
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockImplementationOnce(() => pendingRun);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "primeira consulta");
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    expect(dock.getByText("Analisando…")).toBeVisible();

    await user.type(dock.getByLabelText("Mensagem para o agente"), "segunda");
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(runCalls(fetchSpy)).toHaveLength(1);

    resolveRun(
      agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "primeira resposta",
        sources: null,
        correlationId,
      }),
    );
    expect(await dock.findByText("primeira resposta")).toBeVisible();

    fetchSpy
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockResolvedValueOnce(
        agentRunResponse({
          status: "result",
          reasonCode: "ok",
          text: "segunda resposta",
          sources: null,
          correlationId,
        }),
      );
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    expect(await dock.findByText("segunda resposta")).toBeVisible();
    expect(runCalls(fetchSpy)).toHaveLength(2);

    const [firstBody, secondBody] = runCalls(fetchSpy).map(
      ([, init]) => JSON.parse(init.body as string) as { requestId: string },
    );
    expect(firstBody.requestId).not.toBe(secondBody.requestId);
  });

  it("discards a late reply after resetting the conversation", async () => {
    let resolveRun: (value: Response) => void = () => {};
    const pendingRun = new Promise<Response>(
      (resolve) => (resolveRun = resolve),
    );
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(antiforgeryTokenResponse())
        .mockImplementationOnce(() => pendingRun),
    );
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "consulta cancelada");
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    await user.click(
      dock.getByRole("button", { name: "Reiniciar conversa do agente" }),
    );

    resolveRun(
      agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "resposta tardia",
        sources: null,
        correlationId,
      }),
    );
    await waitFor(() =>
      expect(dock.getByRole("note")).toHaveTextContent(
        /consulta independente/i,
      ),
    );
    expect(dock.queryByText("resposta tardia")).toBeNull();
    expect(dock.queryByText("consulta cancelada")).toBeNull();
  });

  it("shares the conversation between the dock and the dedicated page", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(antiforgeryTokenResponse())
        .mockResolvedValueOnce(
          agentRunResponse({
            status: "result",
            reasonCode: "ok",
            text: "resposta compartilhada",
            sources: null,
            correlationId,
          }),
        ),
    );
    const user = userEvent.setup();
    const { router } = await renderAgentWorkspace();

    const dock = await openDockAndType(user, "mensagem compartilhada");
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    expect(await dock.findByText("resposta compartilhada")).toBeVisible();

    const expandButtons = screen.getAllByRole("button", {
      name: "Abrir página dedicada do agente",
    });
    await user.click(expandButtons[expandButtons.length - 1]);
    await waitFor(() => expect(router.state.location.pathname).toBe("/agente"));
    expect(
      await screen.findByRole("heading", { name: "Agente" }),
    ).toBeVisible();
    expect(
      screen.getAllByText("mensagem compartilhada").length,
    ).toBeGreaterThan(0);

    await user.click(
      screen.getByRole("button", { name: "Voltar ao painel do agente" }),
    );
    expect(router.state.location.pathname).toBe("/");
    expect(
      screen.getAllByRole("complementary", { name: "Painel do agente" }),
    ).toHaveLength(1);
    expect(
      screen.getAllByText("mensagem compartilhada").length,
    ).toBeGreaterThan(0);
  });

  it("aborts and discards a pending reply when the workspace unmounts", async () => {
    let resolveRun: (value: Response) => void = () => {};
    const pendingRun = new Promise<Response>(
      (resolve) => (resolveRun = resolve),
    );
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockImplementationOnce(() => pendingRun);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    const { unmount } = await renderAgentWorkspace();

    const dock = await openDockAndType(user, "consulta desmontada");
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    await waitFor(() => expect(runCalls(fetchSpy)).toHaveLength(1));
    const runSignal = runCalls(fetchSpy)[0][1].signal as AbortSignal;
    expect(runSignal.aborted).toBe(false);

    unmount();
    expect(runSignal.aborted).toBe(true);

    resolveRun(
      agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "resposta pós-unmount",
        sources: null,
        correlationId,
      }),
    );
    // Sem erro de act/setState após unmount: a resposta tardia foi descartada
    // pelo epoch e nenhuma atualização de estado acontece.
    await waitFor(() => expect(runSignal.aborted).toBe(true));
  });

  it("aborts, clears the conversation and switches the caption when the active company changes", async () => {
    let snapshot: BootstrapSnapshot | null = null;
    const gateway: MockSessionGateway = {
      login: async () => {
        if (!snapshot) throw new Error("sessão de teste não instalada");
        return structuredClone(snapshot);
      },
      logout: async () => {
        snapshot = null;
      },
      getBootstrap: async () => (snapshot ? structuredClone(snapshot) : null),
    };
    const { createBootstrapSnapshot, mockSessionProfiles } = await import(
      "../auth/mock-session-fixtures"
    );
    snapshot = createBootstrapSnapshot(mockSessionProfiles.maria);
    const betaFirst: BootstrapSnapshot = {
      ...snapshot,
      companies: [...snapshot.companies].reverse(),
    };

    const user = userEvent.setup();
    let resolveRun: (value: Response) => void = () => {};
    const pendingRun = new Promise<Response>(
      (resolve) => (resolveRun = resolve),
    );
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockImplementationOnce(() => pendingRun);
    vi.stubGlobal("fetch", fetchSpy);
    await renderAgentWorkspace("/", gateway);

    const dock = await openDockAndType(user, "consulta da Alfa");
    expect(dock.getByText("Alfa · Consultas independentes")).toBeVisible();
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    await waitFor(() => expect(runCalls(fetchSpy)).toHaveLength(1));
    const [firstPath, firstInit] = runCalls(fetchSpy)[0];
    expect(firstPath).toBe(agentRunsPath);
    expect(JSON.parse(firstInit.body as string)).toMatchObject({
      companyId: alfaCompanyId,
    });
    const runSignal = firstInit.signal as AbortSignal;

    // O drawer modal aplica aria-hidden ao fundo: o gatilho de refresh é
    // consultado com hidden enquanto o painel permanece aberto.
    snapshot = betaFirst;
    await user.click(
      screen.getByRole("button", { name: "Atualizar sessão", hidden: true }),
    );

    expect(
      await dock.findByText("Beta · Consultas independentes"),
    ).toBeVisible();
    expect(runSignal.aborted).toBe(true);
    await waitFor(() =>
      expect(dock.getByRole("note")).toHaveTextContent(
        /consulta independente/i,
      ),
    );
    expect(dock.queryByText("consulta da Alfa")).toBeNull();

    resolveRun(
      agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "resposta tardia da Alfa",
        sources: null,
        correlationId,
      }),
    );
    await waitFor(() => expect(runSignal.aborted).toBe(true));
    expect(dock.queryByText("resposta tardia da Alfa")).toBeNull();
  });
});
