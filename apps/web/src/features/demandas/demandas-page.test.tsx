import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import { atendimentoClient } from "../atendimento/atendimento-client";
import type {
  BootstrapSnapshot,
  EffectiveCapability,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import {
  type DemandBoardCard,
  type DemandDetail,
  demandasClient,
} from "./demandas-client";
import { DemandasPage } from "./demandas-page";

const alfaCompanyId = "11111111-1111-4111-8111-111111111111";
const betaCompanyId = "22222222-2222-4222-8222-222222222222";

const initiationCard: DemandBoardCard = {
  demandDisplayNumber: "DEM-000001",
  demandNumber: 1,
  effectiveDeadline: new Date(Date.now() + 3_600_000).toISOString(),
  id: "33333333-3333-4333-8333-333333333333",
  isOverdue: false,
  automationPaused: false,
  priority: "critical",
  responsibleMembershipId: "55555555-5555-4555-8555-555555555555",
  stage: "initiation",
  stageStartedAt: new Date().toISOString(),
  title: "Pagar fornecedor",
  version: 1,
};

const initiationDetail: DemandDetail = {
  demand: {
    companyId: alfaCompanyId,
    cycleCount: 1,
    departamento: null,
    description: "Descrição da demanda",
    demandDisplayNumber: "DEM-000001",
    demandNumber: 1,
    effectiveDeadline: null,
    id: initiationCard.id,
    isOverdue: false,
    automationPaused: false,
    manualDueAt: null,
    priority: "high",
    responsibleMembershipId: "55555555-5555-4555-8555-555555555555",
    routineId: null,
    slaDueAt: null,
    stage: "initiation",
    stageDueAt: null,
    tipo: null,
    title: "Pagar fornecedor",
    version: 1,
  },
  checklist: [],
  history: [],
  participants: [],
};

const overdueCard: DemandBoardCard = {
  ...initiationCard,
  demandDisplayNumber: "DEM-000002",
  demandNumber: 2,
  effectiveDeadline: new Date(Date.now() - 7_200_000).toISOString(),
  id: "44444444-4444-4444-8444-444444444444",
  isOverdue: true,
  stage: "execution",
  title: "Fechamento contábil",
};

// Responsável fora do diretório: o avatar cai no fallback neutro.
const unknownResponsibleCard: DemandBoardCard = {
  ...initiationCard,
  demandDisplayNumber: "DEM-000003",
  demandNumber: 3,
  id: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  responsibleMembershipId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
  stage: "execution",
  title: "Conciliar extratos",
};

const readCapability = {
  name: "demandas.demand.read",
  scopes: ["Company"],
} as const;
const createCapability = {
  name: "demandas.demand.create",
  scopes: ["Company"],
} as const;
const advanceCapability = {
  name: "demandas.demand.advance",
  scopes: ["Company"],
} as const;
const allDemandasCapabilities = [
  readCapability,
  createCapability,
  advanceCapability,
];

function snapshot(
  capabilities: readonly EffectiveCapability[] = allDemandasCapabilities,
): BootstrapSnapshot {
  return {
    companies: [
      {
        capabilities,
        companyId: alfaCompanyId,
        companyName: "Alfa",
        membershipId: "55555555-5555-4555-8555-555555555555",
      },
      {
        capabilities,
        companyId: betaCompanyId,
        companyName: "Beta",
        membershipId: "66666666-6666-4666-8666-666666666666",
      },
    ],
    flags: {},
    platform: { isPlatformOperator: false },
    user: {
      displayName: "Ana",
      id: "77777777-7777-4777-8777-777777777777",
    },
  };
}

function gatewayFor(getBootstrap = async () => snapshot()): MockSessionGateway {
  return {
    getBootstrap,
    login: async () => snapshot(),
    logout: async () => {},
  };
}

function renderPage(
  capabilities: readonly EffectiveCapability[] = allDemandasCapabilities,
) {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gatewayFor(async () => snapshot(capabilities))}>
        <DemandasPage />
      </SessionProvider>
    </VinxThemeProvider>,
  );
}

afterEach(() => vi.restoreAllMocks());

describe("DemandasPage", () => {
  it("renders the honest stage columns and advances by explicit command only", async () => {
    const advanceSpy = vi.spyOn(demandasClient, "advance").mockResolvedValue({
      ok: true,
      demand: { ...initiationCard, stage: "execution", version: 2 },
    });
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard],
      total: 1,
      hasMore: false,
      nextCursor: null,
    });

    renderPage();

    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();
    expect(screen.getByText("Em iniciação")).toBeVisible();
    expect(screen.getByText("Aguardando aprovação")).toBeVisible();
    expect(screen.queryByText(/arraste/i)).not.toBeInTheDocument();

    await userEvent.click(
      await screen.findByTestId(`advance-${initiationCard.id}`),
    );
    await waitFor(() => expect(advanceSpy).toHaveBeenCalledOnce());
  });

  it("applies the full conflict snapshot so the card moves to the server stage", async () => {
    // 409 devolve version E stage: aplicar só a versão deixava o cartão na
    // coluna antiga e o retry avançava de novo (bloqueio de revisão).
    vi.spyOn(demandasClient, "advance").mockResolvedValue({
      ok: false,
      code: "integrity_conflict",
      current: { stage: "execution", version: 5 },
    });
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard],
      total: 1,
      hasMore: false,
      nextCursor: null,
    });

    renderPage([readCapability, advanceCapability]);

    await screen.findByText("Pagar fornecedor");
    await userEvent.click(
      await screen.findByTestId(`advance-${initiationCard.id}`),
    );

    expect(
      await screen.findByText(/atualizada por outra pessoa/),
    ).toBeVisible();
    const executionHeading = screen.getByText("Em execução");
    let container: HTMLElement | null = executionHeading.parentElement;
    while (container && !within(container).queryByText("Pagar fornecedor")) {
      container = container.parentElement;
    }
    expect(
      container && within(container).queryByText("Pagar fornecedor"),
    ).toBeTruthy();
    const initiationHeading = screen.getByText("Em iniciação");
    let initiationContainer: HTMLElement | null =
      initiationHeading.parentElement;
    while (
      initiationContainer &&
      !within(initiationContainer).queryByText("Pagar fornecedor") &&
      initiationContainer !== container?.parentElement
    ) {
      initiationContainer = initiationContainer.parentElement;
    }
  });

  it("hides advance actions from a company with read capability only", async () => {
    const advanceSpy = vi.spyOn(demandasClient, "advance");
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard],
      total: 1,
      hasMore: false,
      nextCursor: null,
    });

    renderPage([readCapability]);

    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();
    expect(
      screen.queryByTestId(`advance-${initiationCard.id}`),
    ).not.toBeInTheDocument();
    expect(advanceSpy).not.toHaveBeenCalled();
  });

  it("does not publish an inert create action", async () => {
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard],
      total: 1,
      hasMore: false,
      nextCursor: null,
    });

    renderPage([readCapability, createCapability, advanceCapability]);

    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();
    expect(screen.queryByTestId("demandas-create")).not.toBeInTheDocument();
  });

  it("surfaces the derived overdue state without an overdue column", async () => {
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard, overdueCard],
      total: 2,
      hasMore: false,
      nextCursor: null,
    });

    renderPage();

    expect(await screen.findByText("Fechamento contábil")).toBeVisible();
    const chips = await screen.findAllByTestId("overdue-chip");
    expect(chips.length).toBe(1);
    expect(screen.queryByText("Atrasos")).not.toBeInTheDocument();
  });

  it("appends the next page through the load-more affordance and hides it at the end", async () => {
    const boardSpy = vi
      .spyOn(demandasClient, "board")
      .mockResolvedValueOnce({
        items: [initiationCard],
        total: 2,
        hasMore: true,
        nextCursor: "cur-1",
      })
      .mockResolvedValueOnce({
        items: [
          {
            ...initiationCard,
            id: "55555555-5555-4555-8555-555555555555",
            demandNumber: 2,
            demandDisplayNumber: "DEM-000002",
            title: "Renovar contrato",
          },
        ],
        total: 2,
        hasMore: false,
        nextCursor: null,
      });

    renderPage();

    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();
    const loadMore = await screen.findByTestId("demandas-load-more");
    expect(loadMore).toBeVisible();

    await userEvent.click(loadMore);
    expect(await screen.findByText("Renovar contrato")).toBeVisible();
    expect(screen.getByText("Pagar fornecedor")).toBeVisible();
    expect(boardSpy).toHaveBeenLastCalledWith(
      alfaCompanyId,
      expect.objectContaining({ after: "cur-1" }),
    );
    await waitFor(() =>
      expect(
        screen.queryByTestId("demandas-load-more"),
      ).not.toBeInTheDocument(),
    );
  });

  it("opens the deep-linked detail dialog with checklist and history", async () => {
    const spy = vi.spyOn(demandasClient, "detail").mockImplementation(
      vi.fn(() =>
        Promise.resolve({
          checklist: [
            {
              doneAt: null,
              id: "chk1",
              isDone: false,
              position: 0,
              text: "Validar nota",
            },
          ],
          demand: {
            companyId: alfaCompanyId,
            cycleCount: 1,
            departamento: null,
            description: "Descrição da demanda",
            demandDisplayNumber: "DEM-000001",
            demandNumber: 1,
            effectiveDeadline: null,
            id: initiationCard.id,
            isOverdue: false,
            automationPaused: false,
            manualDueAt: null,
            priority: "high" as const,
            responsibleMembershipId: "55555555-5555-4555-8555-555555555555",
            routineId: null,
            slaDueAt: null,
            stage: "initiation" as const,
            stageDueAt: null,
            tipo: null,
            title: "Pagar fornecedor",
            version: 1,
          },
          history: [
            {
              actorUserId: null,
              cycleCount: 1,
              fromStage: null,
              id: "evt1",
              kind: "created",
              occurredAt: new Date().toISOString(),
              toStage: "initiation",
            },
          ],
          participants: [],
        }),
      ),
    );
    window.history.replaceState(
      null,
      "",
      `/demandas?demand=${initiationCard.id}`,
    );
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [],
      total: 0,
      hasMore: false,
      nextCursor: null,
    });

    renderPage();

    expect(await screen.findByText("Validar nota")).toBeVisible();
    expect(spy).toHaveBeenCalledWith(alfaCompanyId, initiationCard.id);
    expect(screen.getByTestId("demand-history")).toBeVisible();
  });

  it("discards late board responses from the previous company after a switch", async () => {
    // Resposta da empresa Alfa fica pendurada e só resolve DEPOIS da troca
    // para Beta: a geração de escopo descarta o resultado atrasado e o quadro
    // da empresa nova nunca é sobrescrito pelo da anterior.
    let resolveAlfa: (page: {
      items: DemandBoardCard[];
      total: number;
      hasMore: boolean;
      nextCursor: string | null;
    }) => void = () => {};
    const alfaBoard = new Promise<{
      items: DemandBoardCard[];
      total: number;
      hasMore: boolean;
      nextCursor: string | null;
    }>((resolve) => {
      resolveAlfa = resolve;
    });
    const boardSpy = vi
      .spyOn(demandasClient, "board")
      .mockImplementationOnce(() => alfaBoard)
      .mockResolvedValue({
        items: [{ ...initiationCard, title: "Demanda da Beta" }],
        total: 1,
        hasMore: false,
        nextCursor: null,
      });

    window.history.replaceState(null, "", "/demandas");
    const user = userEvent.setup();
    renderPage();

    // Empresa inicial Alfa: a busca fica pendurada (sem cartões renderizados).
    await waitFor(() => expect(boardSpy).toHaveBeenCalledOnce());

    // Troca para Beta: estado anterior é limpo e o quadro de Beta carrega.
    await user.click(await screen.findByLabelText("Empresa"));
    await user.click(await screen.findByRole("option", { name: "Beta" }));
    expect(await screen.findByText("Demanda da Beta")).toBeVisible();
    expect(boardSpy).toHaveBeenLastCalledWith(betaCompanyId, expect.anything());

    // Resposta tardia de Alfa: descartada pela geração de escopo — o quadro
    // de Beta permanece intacto.
    resolveAlfa({
      items: [initiationCard],
      total: 1,
      hasMore: false,
      nextCursor: null,
    });
    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByText("Pagar fornecedor")).not.toBeInTheDocument();
    expect(screen.getByText("Demanda da Beta")).toBeVisible();
  });

  it("clears the previous company board and detail synchronously on switch", async () => {
    // A troca de empresa limpa cartões/detalhe ANTES de commitar o novo
    // companyId: nenhuma renderização intermediária mostra dados da empresa
    // anterior sob o contexto novo.
    let resolveBeta: (page: {
      items: DemandBoardCard[];
      total: number;
      hasMore: boolean;
      nextCursor: string | null;
    }) => void = () => {};
    const betaBoard = new Promise<{
      items: DemandBoardCard[];
      total: number;
      hasMore: boolean;
      nextCursor: string | null;
    }>((resolve) => {
      resolveBeta = resolve;
    });
    const boardSpy = vi
      .spyOn(demandasClient, "board")
      .mockResolvedValueOnce({
        items: [initiationCard],
        total: 1,
        hasMore: false,
        nextCursor: null,
      })
      .mockImplementationOnce(() => betaBoard);

    window.history.replaceState(null, "", "/demandas");
    const user = userEvent.setup();
    renderPage();
    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();

    await user.click(await screen.findByLabelText("Empresa"));
    await user.click(await screen.findByRole("option", { name: "Beta" }));

    // ANTES de resolver a resposta de Beta: o cartão de Alfa já saiu do DOM.
    expect(screen.queryByText("Pagar fornecedor")).not.toBeInTheDocument();
    expect(boardSpy).toHaveBeenLastCalledWith(betaCompanyId, expect.anything());

    resolveBeta({ items: [], total: 0, hasMore: false, nextCursor: null });
  });

  it("discards a late detail response from the previous company after a switch", async () => {
    let resolveDetail: (detail: DemandDetail) => void = () => {};
    const alfaDetail = new Promise<DemandDetail>((resolve) => {
      resolveDetail = resolve;
    });
    vi.spyOn(demandasClient, "board")
      .mockResolvedValueOnce({
        items: [initiationCard],
        total: 1,
        hasMore: false,
        nextCursor: null,
      })
      .mockResolvedValue({
        items: [
          {
            ...initiationCard,
            id: "88888888-8888-4888-8888-888888888888",
            title: "Demanda exclusiva da Beta",
          },
        ],
        total: 1,
        hasMore: false,
        nextCursor: null,
      });
    const detailSpy = vi
      .spyOn(demandasClient, "detail")
      .mockImplementationOnce(() => alfaDetail)
      .mockResolvedValue({
        ...initiationDetail,
        demand: {
          ...initiationDetail.demand,
          title: "Detalhe da Beta",
          description: "Descrição da Beta",
        },
      });

    window.history.replaceState(null, "", "/demandas");
    const user = userEvent.setup();
    renderPage();
    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();

    // Abre o detalhe de Alfa (fica pendurado) e troca para Beta.
    await user.click(await screen.findByRole("button", { name: "Detalhes" }));
    await user.click(await screen.findByLabelText("Empresa"));
    await user.click(await screen.findByRole("option", { name: "Beta" }));
    expect(window.location.search).toBe("");

    // Detalhe anterior não pode voltar quando resolver: conteúdo exclusivo
    // do detalhe de Alfa jamais aparece (a resposta tardia foi descartada).
    resolveDetail(initiationDetail);
    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByText("Descrição da demanda")).not.toBeInTheDocument();
    // O deep link da empresa anterior não restaura o detalhe: nenhuma
    // segunda chamada de detail e nenhum diálogo reaberto sob a Beta.
    expect(detailSpy).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
  });

  it("renders human initials for directory members and a neutral fallback otherwise", async () => {
    vi.spyOn(atendimentoClient, "directoryOptions").mockResolvedValue([
      {
        displayName: "Bruno Souza",
        employeeId: "77777777-7777-4777-8777-777777777777",
        membershipId: initiationCard.responsibleMembershipId,
      },
    ]);
    vi.spyOn(demandasClient, "board").mockResolvedValue({
      items: [initiationCard, unknownResponsibleCard],
      total: 2,
      hasMore: false,
      nextCursor: null,
    });

    renderPage();

    expect(await screen.findByText("Pagar fornecedor")).toBeVisible();

    // Membro presente no diretório: iniciais do nome de exibição.
    const knownCard = screen.getByTestId(`demand-card-${initiationCard.id}`);
    expect(await within(knownCard).findByText("BS")).toBeVisible();

    // Membro ausente do diretório: fallback neutro, nunca fragmento de UUID.
    const unknownCard = screen.getByTestId(
      `demand-card-${unknownResponsibleCard.id}`,
    );
    expect(within(unknownCard).getByText("?")).toBeVisible();
    expect(within(unknownCard).queryByText("DD")).not.toBeInTheDocument();
  });
});
