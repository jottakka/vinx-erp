import { expect, test } from "@playwright/test";
import { signIn } from "./support/bff-session";
import {
  alfaCompanyId,
  alfaMembershipId,
  betaCompanyId,
  betaMembershipId,
  installModuleSession,
  type ModuleCompanyMembership,
} from "./support/module-session";

const alfaCardId = "c1111111-1111-4111-8111-111111111111";
const betaCardId = "c2222222-2222-4222-8222-222222222222";

function readCompany(
  companyId: string,
  membershipId: string,
  companyName: string,
): ModuleCompanyMembership {
  return {
    capabilities: [{ name: "demandas.demand.read" }],
    companyId,
    companyName,
    membershipId,
  };
}

function boardCard(title: string, cardId: string, stageStartedAt: string) {
  return {
    automationPaused: false,
    demandDisplayNumber: "DEM-0001",
    demandNumber: 1,
    effectiveDeadline: null,
    id: cardId,
    isOverdue: false,
    priority: "medium",
    responsibleMembershipId: alfaMembershipId,
    stage: "initiation",
    stageStartedAt,
    title,
    version: 1,
  };
}

test.describe("Troca de empresa ativa", () => {
  test("switching the active company through the module selector refetches the board with the new company's data", async ({
    page,
  }) => {
    const boardReads: string[] = [];

    await installModuleSession(page, [
      readCompany(alfaCompanyId, alfaMembershipId, "Alfa"),
      readCompany(betaCompanyId, betaMembershipId, "Beta"),
    ]);

    await page.route("**/demandas/board*", async (route) => {
      const companyId = new URL(route.request().url()).searchParams.get(
        "companyId",
      );
      boardReads.push(companyId ?? "");
      const alfaScoped = companyId === alfaCompanyId;
      await route.fulfill({
        body: JSON.stringify({
          hasMore: false,
          items: [
            boardCard(
              alfaScoped ? "Registro anual na Alfa" : "Selo digital na Beta",
              alfaScoped ? alfaCardId : betaCardId,
              new Date().toISOString(),
            ),
          ],
          nextCursor: null,
          total: 1,
        }),
        contentType: "application/json",
        status: 200,
      });
    });

    await page.goto("/demandas");
    await expect(page).toHaveURL(/\/login$/);
    await signIn(page);
    await expect(page).toHaveURL(/\/demandas$/);
    await expect(page.getByRole("heading", { name: "Demandas" })).toBeVisible();

    // Empresa inicial da sessão (primeira autorizada): dados da Alfa.
    await expect(page.getByText("Registro anual na Alfa")).toBeVisible();
    await expect(page.getByText("Selo digital na Beta")).not.toBeVisible();

    // Troca pela seletor real da página (CompanySelect "Empresa").
    await page.getByLabel("Empresa").click();
    await page.getByRole("option", { name: "Beta" }).click();

    await expect(page.getByText("Selo digital na Beta")).toBeVisible();
    await expect(page.getByText("Registro anual na Alfa")).not.toBeVisible();
    expect(boardReads).toEqual([alfaCompanyId, betaCompanyId]);
    expect(new URL(page.url()).search).toBe("");
  });
});
