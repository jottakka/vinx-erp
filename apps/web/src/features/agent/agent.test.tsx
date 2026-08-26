import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useLayoutEffect } from "react";
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
import {
  AgentConversationProvider,
  agentReplyText,
  createAgentConversationScopeKey,
  useAgentConversation,
} from "./agent-conversation-provider";
import { AgentDock } from "./agent-dock";
import { AgentTrigger } from "./agent-trigger";

const agentRunsPath = "/agent/runs";
const antiforgeryPath = "/foundation/identity/browser-session/antiforgery";
const confirmationPath = "/foundation/files/confirmations";
const intentionPath = "/foundation/files/upload-intentions";
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
  const { bootstrap, refreshBootstrap } = useSession();
  return (
    <button
      disabled={!bootstrap}
      onClick={() => void refreshBootstrap()}
      type="button"
    >
      Atualizar sessão
    </button>
  );
}

type ScopeCommitSnapshot = {
  companyId: string | null;
  draft: string;
  membershipId: string | null;
  oldMessageVisible: boolean;
  userId: string | null;
};

type AgentConversationScope = Parameters<
  typeof createAgentConversationScopeKey
>[0];

function ScopeCommitProbe({
  onCommit,
}: {
  onCommit: (snapshot: ScopeCommitSnapshot) => void;
}) {
  const { bootstrap } = useSession();
  const { selectedCompanyId } = useAgentConversation();

  useLayoutEffect(() => {
    const company = bootstrap?.companies.find(
      (candidate) => candidate.companyId === selectedCompanyId,
    );
    const dock = screen.queryAllByRole("complementary", {
      name: "Painel do agente",
    })[0];
    const composer = screen.queryAllByLabelText("Mensagem para o agente")[0] as
      | HTMLTextAreaElement
      | undefined;
    onCommit({
      companyId: company?.companyId ?? null,
      draft: composer?.value ?? "",
      membershipId: company?.membershipId ?? null,
      oldMessageVisible:
        dock?.textContent?.includes("consulta da associação anterior") === true,
      userId: bootstrap?.user.id ?? null,
    });
  }, [bootstrap, onCommit, selectedCompanyId]);

  return null;
}

async function renderAgentWorkspace(
  route = "/",
  gateway: MockSessionGateway = createMockSessionGateway(),
  onScopeCommit?: (snapshot: ScopeCommitSnapshot) => void,
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
          {onScopeCommit ? <ScopeCommitProbe onCommit={onScopeCommit} /> : null}
          <FeedbackProvider>
            <RefreshBootstrapButton />
            <RouterProvider router={router} />
          </FeedbackProvider>
        </AgentConversationProvider>
      </SessionProvider>
    </VinxThemeProvider>,
  );

  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Atualizar sessão" }),
    ).toBeEnabled(),
  );

  return { router, unmount };
}

async function openDockAndSelectCompany(
  user: ReturnType<typeof userEvent.setup>,
  companyName: RegExp = /Alfa/,
) {
  await user.click(
    screen.getByRole("button", { name: /^Abrir painel do agente/ }),
  );
  let dock = within(
    screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
  );
  await user.click(dock.getByRole("combobox", { name: "Empresa do envio" }));
  await user.click(await screen.findByRole("option", { name: companyName }));
  dock = within(
    screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
  );
  return dock;
}

async function openDockAndType(
  user: ReturnType<typeof userEvent.setup>,
  message: string,
) {
  const dock = await openDockAndSelectCompany(user);
  await user.type(dock.getByLabelText("Mensagem para o agente"), message);
  return dock;
}

function muiSelectValue(combobox: HTMLElement): string {
  const input = combobox.parentElement?.querySelector<HTMLInputElement>(
    'input[aria-hidden="true"]',
  );
  if (!input) throw new Error("MUI select hidden input was not found");
  return input.value;
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

function attachmentFile(name: string, bytes = new Uint8Array([1])): File {
  const file = new File([bytes], name, { type: "application/pdf" });
  Object.defineProperty(file, "arrayBuffer", {
    value: async () => bytes.buffer.slice(0),
  });
  return file;
}

function attachmentFetch(
  fileIds: readonly string[],
  blockedAntiforgery?: { at: number; response: Promise<Response> },
  fileIdsByName?: Readonly<Record<string, string>>,
) {
  let antiforgeryCalls = 0;
  let intended = 0;
  return vi.fn(async (path: string, init?: RequestInit) => {
    if (path === antiforgeryPath) {
      antiforgeryCalls += 1;
      if (blockedAntiforgery?.at === antiforgeryCalls) {
        return blockedAntiforgery.response;
      }
      return antiforgeryTokenResponse();
    }
    if (path === intentionPath) {
      const body = JSON.parse(init?.body as string) as { fileName: string };
      const id = fileIdsByName?.[body.fileName] ?? fileIds[intended++];
      return new Response(
        JSON.stringify({
          fileId: id,
          processingState: "quarantined",
          uploadUrl: `https://storage.test.invalid/${id}`,
        }),
        { headers: { "Content-Type": "application/json" }, status: 200 },
      );
    }
    if (path === confirmationPath) {
      const body = JSON.parse(init?.body as string) as { fileId: string };
      return new Response(
        JSON.stringify({ fileId: body.fileId, processingState: "confirmed" }),
        { headers: { "Content-Type": "application/json" }, status: 200 },
      );
    }
    if (path.startsWith("https://storage.test.invalid/")) {
      return new Response(null, { status: 204 });
    }
    if (path === agentRunsPath) {
      return agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "Resposta autorizada",
        sources: null,
        correlationId,
      });
    }
    throw new Error("unexpected request");
  });
}

function createCompanylessGateway(): MockSessionGateway {
  const snapshot: BootstrapSnapshot = {
    companies: [],
    flags: {},
    platform: { isPlatformOperator: false },
    user: { displayName: "Usuária sem empresa", id: "user-without-company" },
  };

  return {
    getBootstrap: async () => structuredClone(snapshot),
    login: async () => structuredClone(snapshot),
    logout: async () => {},
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("agent conversation scope key", () => {
  const originalScope: AgentConversationScope = {
    companyId: "company-1",
    membershipId: "membership-1",
    userId: "user-1",
  };

  it.each([
    ["user", { userId: "user-2" }],
    ["membership", { membershipId: "membership-2" }],
    ["company", { companyId: "company-2" }],
  ] as const)("changes when the %s scope changes", (_field, changedScope) => {
    expect(
      createAgentConversationScopeKey({ ...originalScope, ...changedScope }),
    ).not.toBe(createAgentConversationScopeKey(originalScope));
  });
});

describe("agent attachment clarification", () => {
  it("gives a fixed remove-or-verify instruction for unavailable document context", () => {
    const reply = agentReplyText({
      ok: true,
      run: {
        correlationId,
        reasonCode: "document_context_unavailable",
        sources: null,
        status: "needs_clarification",
        text: null,
      },
    });

    expect(reply).toBe(
      "Não foi possível usar o anexo. Remova-o ou verifique-o antes de tentar novamente.",
    );
    expect(reply).not.toContain("document_context_unavailable");
  });
});

describe("agent access copy", () => {
  it.each([
    [
      "capability_denied",
      "O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.",
    ],
    ["company_required", "Selecione uma empresa para usar o assistente."],
    [
      "company_not_authorized",
      "Você não tem acesso ao assistente nesta empresa. Selecione outra empresa ou solicite acesso ao administrador.",
    ],
  ] as const)(
    "maps %s to actionable copy without exposing the code",
    (reasonCode, expected) => {
      const reply = agentReplyText({
        ok: true,
        run: {
          correlationId,
          reasonCode,
          sources: null,
          status: "denied",
          text: null,
        },
      });

      expect(reply).toBe(expected);
      expect(reply).not.toContain(reasonCode);
    },
  );
});

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
    expect(dock.getAllByText("Alfa", { exact: true }).length).toBeGreaterThan(
      0,
    );
    expect(dock.getByText("Empresa selecionada")).toBeVisible();
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

    expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
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
    await user.type(dock.getByLabelText("Mensagem para o agente"), "segunda");

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
        /Faça uma pergunta sobre a empresa selecionada/i,
      ),
    );
    expect(dock.queryByText("resposta tardia")).toBeNull();
    expect(dock.queryByText("consulta cancelada")).toBeNull();
  });

  it("shares the conversation between the dock and the full-page view", async () => {
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
      name: "Abrir agente em tela inteira",
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

  it("describes the full-page view as real company-scoped provider processing", async () => {
    const user = userEvent.setup();
    await renderAgentWorkspace("/agente");

    expect(
      await screen.findByRole("heading", { name: "Agente" }),
    ).toBeVisible();
    await user.click(
      screen.getByRole("combobox", { name: "Empresa do envio" }),
    );
    await user.click(await screen.findByRole("option", { name: /Alfa/ }));
    expect(screen.queryByText("Dados de demonstração")).toBeNull();
    expect(
      screen.getByText(/Os envios são reais, vinculados à empresa indicada/i),
    ).toBeVisible();
  });

  it("keeps the blocked composer status canonical on the full-page view", async () => {
    await renderAgentWorkspace("/agente", createCompanylessGateway());

    expect(await screen.findByText("Assistente")).toBeVisible();
    expect(
      screen.getByText("Escolha uma empresa antes de enviar uma pergunta."),
    ).toBeVisible();
    expect(
      screen.getAllByText("Selecione uma empresa para usar o assistente."),
    ).toHaveLength(1);
    expect(screen.getByRole("status")).toHaveTextContent(
      "Selecione uma empresa para usar o assistente.",
    );
    expect(
      screen.queryByText(
        /Página dedicada|Consultas independentes|turnos|contexto/i,
      ),
    ).toBeNull();
    expect(screen.queryByText(/vinculados à empresa indicada/i)).toBeNull();
  });

  it("blocks all interaction with no company before any request", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    await renderAgentWorkspace("/", createCompanylessGateway());
    await user.click(
      screen.getByRole("button", {
        name: "Abrir painel do agente — indisponível para este perfil",
      }),
    );
    const dock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    expect(dock.getByRole("status")).toHaveTextContent(
      "Selecione uma empresa para usar o assistente.",
    );
    expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
    const picker = dock.getByLabelText("Anexar documentos ao agente");
    expect(dock.getByRole("button", { name: "Anexar arquivo" })).toBeDisabled();
    expect(picker).toBeDisabled();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    const composer = dock.getByLabelText("Mensagem para o agente");
    expect(composer.closest("form")).toHaveAttribute(
      "aria-describedby",
      "agent-access-message",
    );
    const blockedMessage = "tentativa sem empresa";

    await user.type(composer, blockedMessage);
    await user.upload(picker, attachmentFile("sem-empresa.pdf"));
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(picker).toHaveValue("");
    expect(composer).toHaveValue("");
    expect(
      dock.queryByRole("list", { name: "Anexos selecionados" }),
    ).toBeNull();
    expect(dock.getByRole("log")).not.toHaveTextContent(blockedMessage);
    expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("blocks unavailable capability before any request", async () => {
    const { createBootstrapSnapshot, mockSessionProfiles } = await import(
      "../auth/mock-session-fixtures"
    );
    const snapshot = createBootstrapSnapshot(mockSessionProfiles.maria);
    const withoutAiRead: BootstrapSnapshot = {
      ...snapshot,
      companies: snapshot.companies.map((company, index) =>
        index === 0
          ? {
              ...company,
              capabilities: company.capabilities.filter(
                (capability) => capability.name !== "ai.read",
              ),
            }
          : company,
      ),
    };
    const gateway: MockSessionGateway = {
      getBootstrap: async () => structuredClone(withoutAiRead),
      login: async () => structuredClone(withoutAiRead),
      logout: async () => {},
    };
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    await renderAgentWorkspace("/", gateway);

    await user.click(
      screen.getByRole("button", {
        name: "Abrir painel do agente — indisponível para este perfil",
      }),
    );
    let dock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    await user.click(dock.getByRole("combobox", { name: "Empresa do envio" }));
    await user.click(await screen.findByRole("option", { name: /Alfa/ }));
    dock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );

    expect(dock.getAllByText("Alfa", { exact: true }).length).toBeGreaterThan(
      0,
    );
    expect(dock.getByText("Assistente indisponível")).toBeVisible();
    expect(dock.getByRole("status")).toHaveTextContent(
      "O assistente não está disponível para o seu perfil nesta empresa.",
    );
    expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
    const picker = dock.getByLabelText("Anexar documentos ao agente");
    expect(dock.getByRole("button", { name: "Anexar arquivo" })).toBeDisabled();
    expect(picker).toBeDisabled();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    const composer = dock.getByLabelText("Mensagem para o agente");
    const blockedMessage = "tentativa sem capability";

    await user.type(composer, blockedMessage);
    await user.upload(picker, attachmentFile("sem-capability.pdf"));
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(picker).toHaveValue("");
    expect(composer).toHaveValue("");
    expect(
      dock.queryByRole("list", { name: "Anexos selecionados" }),
    ).toBeNull();
    expect(dock.getByRole("log")).not.toHaveTextContent(blockedMessage);
    expect(dock.getByLabelText("Mensagem para o agente")).toBeDisabled();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("renders a friendly capability denial returned after authorization", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockResolvedValueOnce(
        agentRunResponse({
          status: "denied",
          reasonCode: "capability_denied",
          text: null,
          sources: null,
          correlationId,
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "consulta revogada");
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(
      await dock.findByText(
        "O assistente não está disponível para o seu perfil nesta empresa. Solicite acesso ao administrador da empresa.",
      ),
    ).toBeVisible();
    expect(dock.queryByText("capability_denied")).toBeNull();
    expect(runCalls(fetchSpy)).toHaveLength(1);
  });

  it("keeps the authorized attachment button focusable and opens its chooser", async () => {
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndSelectCompany(user);
    const picker = dock.getByLabelText("Anexar documentos ao agente");
    const openChooser = vi.spyOn(picker, "click");
    const attachmentButton = dock.getByRole("button", {
      name: "Anexar arquivo",
    });

    expect(picker).toBeEnabled();
    expect(attachmentButton).toBeEnabled();
    expect(attachmentButton).not.toHaveAttribute("tabindex", "-1");
    await user.click(attachmentButton);
    expect(openChooser).toHaveBeenCalledOnce();
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

  it("clears messages and the unsent draft when the authenticated membership scope changes", async () => {
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
    const renewedMembership: BootstrapSnapshot = {
      ...snapshot,
      companies: snapshot.companies.map((company, index) =>
        index === 0
          ? {
              ...company,
              companyName: "Alfa renovada",
              membershipId: "88888888-8888-4888-8888-888888888888",
            }
          : company,
      ),
      user: { displayName: "Joana", id: "joana" },
    };
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(antiforgeryTokenResponse())
        .mockResolvedValueOnce(
          agentRunResponse({
            status: "result",
            reasonCode: "ok",
            text: "resposta da associação anterior",
            sources: null,
            correlationId,
          }),
        ),
    );
    const user = userEvent.setup();
    const scopeCommits: ScopeCommitSnapshot[] = [];
    await renderAgentWorkspace("/", gateway, (scope) =>
      scopeCommits.push(scope),
    );

    const dock = await openDockAndType(user, "consulta da associação anterior");
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    expect(
      await dock.findByText("resposta da associação anterior"),
    ).toBeVisible();
    await user.type(
      dock.getByLabelText("Mensagem para o agente"),
      "rascunho privado da associação anterior",
    );

    snapshot = renewedMembership;
    await user.click(
      screen.getByRole("button", { name: "Atualizar sessão", hidden: true }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", {
          name: "Atualizar sessão",
          hidden: true,
        }),
      ).toBeEnabled(),
    );

    let refreshedDock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    await waitFor(() =>
      expect(
        muiSelectValue(
          refreshedDock.getByRole("combobox", { name: "Empresa do envio" }),
        ),
      ).toBe(""),
    );
    expect(
      scopeCommits.filter((scope) => scope.userId === null).at(-1),
    ).toEqual({
      companyId: null,
      draft: "",
      membershipId: null,
      oldMessageVisible: false,
      userId: null,
    });
    expect(scopeCommits.find((scope) => scope.userId === "joana")).toEqual({
      companyId: null,
      draft: "",
      membershipId: null,
      oldMessageVisible: false,
      userId: "joana",
    });
    expect
      .soft(refreshedDock.queryByText("consulta da associação anterior"))
      .toBeNull();
    expect
      .soft(refreshedDock.queryByText("resposta da associação anterior"))
      .toBeNull();
    expect(refreshedDock.getByLabelText("Mensagem para o agente")).toHaveValue(
      "",
    );
    await user.click(
      refreshedDock.getByRole("combobox", { name: "Empresa do envio" }),
    );
    await user.click(
      await screen.findByRole("option", { name: /Alfa renovada/ }),
    );
    refreshedDock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    expect(
      (await refreshedDock.findAllByText("Alfa renovada", { exact: true }))
        .length,
    ).toBeGreaterThan(0);
    expect(
      scopeCommits.find(
        (scope) =>
          scope.membershipId === "88888888-8888-4888-8888-888888888888",
      ),
    ).toMatchObject({
      companyId: alfaCompanyId,
      draft: "",
      oldMessageVisible: false,
      userId: "joana",
    });
  });

  it("aborts before the first new-scope layout commit and switches the caption when the active company changes", async () => {
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
    snapshot = betaFirst;

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
    let observeNewScope = false;
    let oldRunSignal: AbortSignal | undefined;
    let oldRunAbortedAtFirstNewScopeLayoutCommit: boolean | undefined;
    await renderAgentWorkspace("/", gateway, () => {
      if (
        observeNewScope &&
        oldRunAbortedAtFirstNewScopeLayoutCommit === undefined
      ) {
        oldRunAbortedAtFirstNewScopeLayoutCommit = oldRunSignal?.aborted;
      }
    });

    const dock = await openDockAndType(user, "consulta da Alfa");
    expect(dock.getAllByText("Alfa", { exact: true }).length).toBeGreaterThan(
      0,
    );
    await user.click(dock.getByRole("button", { name: "Enviar" }));
    await waitFor(() => expect(runCalls(fetchSpy)).toHaveLength(1));
    const [firstPath, firstInit] = runCalls(fetchSpy)[0];
    expect(firstPath).toBe(agentRunsPath);
    expect(JSON.parse(firstInit.body as string)).toMatchObject({
      companyId: alfaCompanyId,
    });
    oldRunSignal = firstInit.signal as AbortSignal;

    observeNewScope = true;
    await user.click(dock.getByRole("combobox", { name: "Empresa do envio" }));
    await user.click(await screen.findByRole("option", { name: /Beta/ }));

    expect(oldRunAbortedAtFirstNewScopeLayoutCommit).toBe(true);

    const refreshedDock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    expect(
      (await refreshedDock.findAllByText("Beta", { exact: true }))[0],
    ).toBeVisible();
    expect(oldRunSignal.aborted).toBe(true);
    await waitFor(() =>
      expect(refreshedDock.getByRole("note")).toHaveTextContent(
        /Faça uma pergunta sobre a empresa selecionada/i,
      ),
    );
    expect(refreshedDock.queryByText("consulta da Alfa")).toBeNull();

    resolveRun(
      agentRunResponse({
        status: "result",
        reasonCode: "ok",
        text: "resposta tardia da Alfa",
        sources: null,
        correlationId,
      }),
    );
    await waitFor(() => expect(oldRunSignal?.aborted).toBe(true));
    expect(refreshedDock.queryByText("resposta tardia da Alfa")).toBeNull();
  });

  it("keeps three ready attachments in selection order and shares them with the dedicated page", async () => {
    const fileIds = [
      "44444444-4444-4444-8444-444444444444",
      "55555555-5555-4555-8555-555555555555",
      "66666666-6666-4666-8666-666666666666",
    ];
    vi.stubGlobal(
      "fetch",
      attachmentFetch(fileIds, undefined, {
        "primeiro.pdf": fileIds[0],
        "segundo.pdf": fileIds[1],
        "terceiro.pdf": fileIds[2],
      }),
    );
    const user = userEvent.setup();
    const { router } = await renderAgentWorkspace();
    const dock = await openDockAndSelectCompany(user);

    await user.upload(dock.getByLabelText("Anexar documentos ao agente"), [
      attachmentFile("primeiro.pdf"),
      attachmentFile("segundo.pdf"),
      attachmentFile("terceiro.pdf"),
    ]);

    await waitFor(() => expect(dock.getAllByText("Pronto")).toHaveLength(3));
    const selected = dock.getByRole("list", { name: "Anexos selecionados" });
    expect(
      within(selected)
        .getAllByRole("listitem")
        .map((item) => item.textContent),
    ).toEqual([
      expect.stringContaining("primeiro.pdf"),
      expect.stringContaining("segundo.pdf"),
      expect.stringContaining("terceiro.pdf"),
    ]);

    await user.click(
      dock.getByRole("button", { name: "Abrir agente em tela inteira" }),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe("/agente"));
    expect(
      await screen.findByRole("list", { name: "Anexos selecionados" }),
    ).toHaveTextContent("primeiro.pdf");
  });

  it("clears the selection-limit notice after removing one of three ready attachments", async () => {
    const fetchSpy = attachmentFetch([
      "44444444-4444-4444-8444-444444444444",
      "55555555-5555-4555-8555-555555555555",
      "66666666-6666-4666-8666-666666666666",
    ]);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndSelectCompany(user);

    await user.upload(dock.getByLabelText("Anexar documentos ao agente"), [
      attachmentFile("um.pdf"),
      attachmentFile("dois.pdf"),
      attachmentFile("tres.pdf"),
    ]);
    await waitFor(() => expect(dock.getAllByText("Pronto")).toHaveLength(3));

    await user.upload(dock.getByLabelText("Anexar documentos ao agente"), [
      attachmentFile("quatro.pdf"),
    ]);

    expect(await dock.findByText(/no máximo três anexos/i)).toBeVisible();
    await user.click(dock.getByRole("button", { name: "Remover um.pdf" }));
    expect(dock.queryByText(/no máximo três anexos/i)).toBeNull();
  });

  it("blocks partial send while a ready attachment has a pending companion", async () => {
    let resolveToken: (response: Response) => void = () => {};
    const token = new Promise<Response>((resolve) => (resolveToken = resolve));
    const fetchSpy = attachmentFetch(["44444444-4444-4444-8444-444444444444"], {
      at: 3,
      response: token,
    });
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndType(user, "resuma o anexo");

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("pronto.pdf"),
    );
    expect(await dock.findByText("Pronto")).toBeVisible();

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("pendente.pdf"),
    );
    await waitFor(() => expect(dock.getByText("Enviando")).toBeVisible());
    const antiforgeryCalls = fetchSpy.mock.calls.filter(
      ([path]) => path === antiforgeryPath,
    );
    const signal = antiforgeryCalls[2][1]?.signal as AbortSignal;
    expect(dock.getByText("Enviando")).toBeVisible();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(runCalls(fetchSpy)).toHaveLength(0);

    await user.click(
      dock.getByRole("button", { name: "Remover pendente.pdf" }),
    );
    expect(signal.aborted).toBe(true);
    expect(dock.queryByText("pendente.pdf")).toBeNull();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeEnabled();
    resolveToken(antiforgeryTokenResponse());
  });

  it("keeps the typed text when Enter is pressed while an attachment is still uploading", async () => {
    let resolveToken: (response: Response) => void = () => {};
    const token = new Promise<Response>((resolve) => (resolveToken = resolve));
    const fetchSpy = attachmentFetch([], { at: 1, response: token });
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndSelectCompany(user);

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("lento.pdf"),
    );
    await waitFor(() => expect(dock.getByText("Enviando")).toBeVisible());

    const composer = dock.getByLabelText("Mensagem para o agente");
    await user.type(composer, "resuma o anexo quando terminar");
    await user.keyboard("{Enter}");

    expect(composer).toHaveValue("resuma o anexo quando terminar");
    // O texto não vira mensagem: só pode existir no próprio compositor.
    expect(
      dock.queryByText("resuma o anexo quando terminar", { selector: "p" }),
    ).toBeNull();
    expect(runCalls(fetchSpy)).toHaveLength(0);

    resolveToken(antiforgeryTokenResponse());
    // Consome o assentamento do upload dentro do próprio teste (pronto ou
    // falho): o fetch stubado não pode vazar para o spy do teste seguinte.
    await waitFor(() =>
      expect(dock.queryByText("Enviando")).not.toBeInTheDocument(),
    );
  });

  it("sends three ready IDs in order after rejecting a fourth and clears the warning", async () => {
    const fileIds = [
      "44444444-4444-4444-8444-444444444444",
      "55555555-5555-4555-8555-555555555555",
      "66666666-6666-4666-8666-666666666666",
    ];
    // IDs por nome de arquivo (ordem de assentamento dos uploads não é
    // garantida sob carga); a ordem do corpo não é contrato do produto.
    const fetchSpy = attachmentFetch(fileIds, undefined, {
      "primeiro.pdf": fileIds[0],
      "segundo.pdf": fileIds[1],
      "terceiro.pdf": fileIds[2],
    });
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndType(user, "resuma o anexo pronto");

    await user.upload(dock.getByLabelText("Anexar documentos ao agente"), [
      attachmentFile("primeiro.pdf"),
      attachmentFile("segundo.pdf"),
      attachmentFile("terceiro.pdf"),
    ]);
    await waitFor(() => expect(dock.getAllByText("Pronto")).toHaveLength(3));
    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("quarto.pdf"),
    );
    expect(await dock.findByText(/no máximo três anexos/i)).toBeVisible();
    await user.click(dock.getByRole("button", { name: "Enviar" }));

    expect(await dock.findByText("Resposta autorizada")).toBeVisible();
    const body = JSON.parse(runCalls(fetchSpy)[0][1].body as string);
    expect([...body.attachments].sort()).toEqual([...fileIds].sort());
    expect(
      dock.queryByRole("list", { name: "Anexos selecionados" }),
    ).toBeNull();
    expect(dock.queryByText(/no máximo três anexos/i)).toBeNull();
  });

  it("blocks partial send when a ready attachment has a failed companion", async () => {
    const fetchSpy = attachmentFetch(["44444444-4444-4444-8444-444444444444"]);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();
    const dock = await openDockAndType(user, "resuma o anexo inválido");

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("pronto.pdf"),
    );
    expect(await dock.findByText("Pronto")).toBeVisible();

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("vazio.pdf", new Uint8Array()),
    );

    expect(await dock.findByText("Falhou")).toBeVisible();
    expect(
      dock.getByText("O anexo não atende aos requisitos permitidos."),
    ).toBeVisible();
    expect(dock.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(runCalls(fetchSpy)).toHaveLength(0);
    await user.click(dock.getByRole("button", { name: "Remover vazio.pdf" }));
    expect(dock.getByRole("button", { name: "Enviar" })).toBeEnabled();
  });

  it("aborts and clears attachment uploads on reset and workspace unmount", async () => {
    let resolveFirst: (response: Response) => void = () => {};
    let resolveSecond: (response: Response) => void = () => {};
    const first = new Promise<Response>((resolve) => (resolveFirst = resolve));
    const second = new Promise<Response>(
      (resolve) => (resolveSecond = resolve),
    );
    const fetchSpy = vi
      .fn<typeof fetch>()
      .mockImplementationOnce(() => first)
      .mockImplementationOnce(() => second);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    const { unmount } = await renderAgentWorkspace();
    const dock = await openDockAndSelectCompany(user);

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("reset.pdf"),
    );
    await waitFor(() => expect(fetchSpy).toHaveBeenCalledTimes(1));
    const resetSignal = fetchSpy.mock.calls[0][1]?.signal as AbortSignal;
    await user.click(
      dock.getByRole("button", { name: "Reiniciar conversa do agente" }),
    );
    expect(resetSignal.aborted).toBe(true);
    expect(dock.queryByText("reset.pdf")).toBeNull();

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("unmount.pdf"),
    );
    await waitFor(() => expect(fetchSpy).toHaveBeenCalledTimes(2));
    const unmountSignal = fetchSpy.mock.calls[1][1]?.signal as AbortSignal;
    unmount();
    expect(unmountSignal.aborted).toBe(true);
    resolveFirst(antiforgeryTokenResponse());
    resolveSecond(antiforgeryTokenResponse());
  });

  it("aborts and discards a pending attachment before the new membership scope commits", async () => {
    let snapshot: BootstrapSnapshot | null = null;
    const gateway: MockSessionGateway = {
      login: async () => {
        if (!snapshot) throw new Error();
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
    // O escopo da conversa só reseta quando a identidade/empresa realmente
    // muda: renovar o membershipId da Alfa é uma mudança real de escopo.
    const nextScope = {
      ...snapshot,
      companies: snapshot.companies.map((company) =>
        company.companyId === alfaCompanyId
          ? {
              ...company,
              membershipId: "88888888-8888-4888-8888-888888888888",
            }
          : company,
      ),
    };
    let resolveToken: (response: Response) => void = () => {};
    const token = new Promise<Response>((resolve) => (resolveToken = resolve));
    const fetchSpy = vi.fn<typeof fetch>().mockImplementationOnce(() => token);
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace("/", gateway);
    let dock = await openDockAndSelectCompany(user);

    await user.upload(
      dock.getByLabelText("Anexar documentos ao agente"),
      attachmentFile("escopo-antigo.pdf"),
    );
    await waitFor(() => expect(fetchSpy).toHaveBeenCalledTimes(1));
    const signal = fetchSpy.mock.calls[0][1]?.signal as AbortSignal;
    snapshot = nextScope;
    await user.click(
      screen.getByRole("button", { name: "Atualizar sessão", hidden: true }),
    );

    expect(signal.aborted).toBe(true);
    expect(screen.queryByText("escopo-antigo.pdf")).toBeNull();
    dock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    expect(
      muiSelectValue(dock.getByRole("combobox", { name: "Empresa do envio" })),
    ).toBe("");
    await user.click(dock.getByRole("combobox", { name: "Empresa do envio" }));
    await user.click(await screen.findByRole("option", { name: /Beta/ }));
    dock = within(
      screen.getAllByRole("complementary", { name: "Painel do agente" })[0],
    );
    expect(
      (await dock.findAllByText("Beta", { exact: true }))[0],
    ).toBeVisible();
    resolveToken(antiforgeryTokenResponse());
  });

  it("sends on Enter (without Shift) and keeps Shift+Enter for a newline", async () => {
    const fetchSpy = vi
      .fn()
      .mockResolvedValueOnce(antiforgeryTokenResponse())
      .mockResolvedValueOnce(
        agentRunResponse({
          correlationId,
          reasonCode: "ok",
          sources: null,
          status: "result",
          text: "Envio por Enter",
        }),
      );
    vi.stubGlobal("fetch", fetchSpy);
    const user = userEvent.setup();
    await renderAgentWorkspace();

    const dock = await openDockAndType(user, "ola");
    await user.keyboard("{Enter}");

    expect(await dock.findByText("Envio por Enter")).toBeVisible();
    const calls = runCalls(fetchSpy);
    expect(calls).toHaveLength(1);
    const body = JSON.parse(calls[0][1].body as string);
    expect(body.message).toBe("ola");
  });
});
