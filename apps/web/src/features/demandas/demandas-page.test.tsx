import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VinxThemeProvider } from "../../app/theme/vinx-theme-provider";
import type {
  BootstrapSnapshot,
  EffectiveCapability,
  MockSessionGateway,
} from "../auth/session-contract";
import { SessionProvider } from "../auth/session-provider";
import { type DemandBoardCard, demandasClient } from "./demandas-client";
import { DemandasPage } from "./demandas-page";

const alfaCompanyId = "11111111-1111-4111-8111-111111111111";

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

function renderPage() {
  render(
    <VinxThemeProvider>
      <SessionProvider gateway={gatewayFor()}>
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
});
