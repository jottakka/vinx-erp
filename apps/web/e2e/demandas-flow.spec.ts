import { expect, type Page, test } from "@playwright/test";
import {
  alfaCompanyId,
  alfaMembershipId,
  canonicalRequestIdPattern,
  csrfToken,
  installModuleSession,
  openModulePage,
} from "./support/module-session";

const demandId = "d1111111-1111-4111-8111-111111111111";
const demandTitle = "Implantar agenda tributária";

function cardJson(
  overrides: Partial<ReturnType<typeof cardJsonBase>> = {},
): ReturnType<typeof cardJsonBase> {
  return { ...cardJsonBase(), ...overrides };
}

function cardJsonBase() {
  return {
    automationPaused: false,
    demandDisplayNumber: "DEM-0042",
    demandNumber: 42,
    effectiveDeadline: null,
    id: demandId,
    isOverdue: false,
    priority: "high",
    responsibleMembershipId: alfaMembershipId,
    stage: "initiation",
    stageStartedAt: new Date().toISOString(),
    title: demandTitle,
    version: 1,
  };
}

function columnBoard(page: Page, label: string) {
  return page
    .getByRole("heading", { name: label })
    .locator("xpath=ancestor::div[1]");
}

test.describe("Demandas", () => {
  test.beforeEach(async ({ page }) => {
    await installModuleSession(page, [
      {
        capabilities: [
          { name: "demandas.demand.advance" },
          { name: "demandas.demand.read" },
        ],
        companyId: alfaCompanyId,
        companyName: "Alfa",
        membershipId: alfaMembershipId,
      },
    ]);

    await page.route("**/demandas/board*", async (route) => {
      const request = route.request();
      expect(request.method()).toBe("GET");
      const url = new URL(request.url());
      expect(url.searchParams.get("companyId")).toBe(alfaCompanyId);
      await route.fulfill({
        body: JSON.stringify({
          hasMore: false,
          items: [cardJson()],
          nextCursor: null,
          total: 1,
        }),
        contentType: "application/json",
        status: 200,
      });
    });
  });

  test("disables the advance button while the command is pending and moves the card on success", async ({
    page,
  }) => {
    let releaseAdvance: () => void = () => {};
    const advanceGate = new Promise<void>((resolve) => {
      releaseAdvance = resolve;
    });
    const advanceCalls: { expectedVersion: number }[] = [];

    await page.route("**/demandas/demands/*/advance", async (route) => {
      const request = route.request();
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      const payload = request.postDataJSON();
      expect(payload.requestId).toMatch(canonicalRequestIdPattern);
      advanceCalls.push({ expectedVersion: payload.expectedVersion });
      if (advanceCalls.length === 1) await advanceGate;
      await route.fulfill({
        body: JSON.stringify({
          demand: cardJson({ stage: "execution", version: 2 }),
        }),
        contentType: "application/json",
        status: 200,
      });
    });

    await openModulePage(page, "/demandas");
    await expect(
      columnBoard(page, "Em iniciação").getByText(demandTitle),
    ).toBeVisible();

    const advanceButton = page.getByRole("button", { name: "Avançar" });
    await advanceButton.click();

    // Duplo clique: um avanço por cartão em voo; o comando só reenvia depois
    // de resolvido (a asserção abaixo falharia se um segundo POST partisse).
    await expect(advanceButton).toBeDisabled();
    releaseAdvance();

    await expect(
      columnBoard(page, "Em execução").getByText(demandTitle),
    ).toBeVisible();
    await expect(advanceButton).toBeEnabled();
    expect(advanceCalls).toEqual([{ expectedVersion: 1 }]);
  });

  test("surfaces the sync message on a 409 conflict and retries with the refreshed version", async ({
    page,
  }) => {
    const advancePayloads: { expectedVersion: number }[] = [];

    await page.route("**/demandas/demands/*/advance", async (route) => {
      const request = route.request();
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      const payload = request.postDataJSON();
      advancePayloads.push({ expectedVersion: payload.expectedVersion });
      if (advancePayloads.length === 1) {
        await route.fulfill({
          body: JSON.stringify({
            code: "integrity_conflict",
            current: { stage: "execution", version: 5 },
          }),
          contentType: "application/json",
          status: 409,
        });
        return;
      }
      await route.fulfill({
        body: JSON.stringify({
          demand: cardJson({ stage: "execution", version: 6 }),
        }),
        contentType: "application/json",
        status: 200,
      });
    });

    await openModulePage(page, "/demandas");

    await page.getByRole("button", { name: "Avançar" }).click();
    await expect(
      page.getByText(
        "A demanda foi atualizada por outra pessoa. O quadro foi sincronizado.",
      ),
    ).toBeVisible();
    await expect(
      columnBoard(page, "Em iniciação").getByText(demandTitle),
    ).toBeVisible();

    await page.getByRole("button", { name: "Avançar" }).click();

    await expect(
      columnBoard(page, "Em execução").getByText(demandTitle),
    ).toBeVisible();
    expect(advancePayloads).toEqual([
      { expectedVersion: 1 },
      { expectedVersion: 5 },
    ]);
  });
});
