import { expect, type Page, test } from "@playwright/test";
import {
  alfaCompanyId,
  alfaMembershipId,
  canonicalRequestIdPattern,
  csrfToken,
  installModuleSession,
  openModulePage,
} from "./support/module-session";

const cardId = "a1111111-1111-4111-8111-111111111111";

function cardJson(
  overrides: Partial<ReturnType<typeof cardJsonBase>> = {},
): ReturnType<typeof cardJsonBase> {
  return { ...cardJsonBase(), ...overrides };
}

function cardJsonBase() {
  return {
    assigneeMembershipId: null,
    attachmentFileId: null,
    categoryRefId: null,
    channel: "web",
    channelRefId: null,
    column: "novo",
    companyId: alfaCompanyId,
    createdAt: "2026-09-01T08:00:00+00:00",
    description: "Cliente relata cobrança duplicada na fatura",
    id: cardId,
    isOverdue: false,
    participantMembershipIds: [] as string[],
    priority: "alta",
    requesterPersonRef: null,
    slaDueAt: null,
    title: "Cobrança indevida",
    updatedAt: "2026-09-01T08:00:00+00:00",
    version: 1,
    watcherMembershipIds: null,
  };
}

function columnBoard(page: Page, label: string) {
  return page
    .getByRole("heading", { name: label })
    .locator("xpath=ancestor::div[1]");
}

async function moveTo(page: Page, destination: string) {
  await page.getByRole("button", { name: "Alterar status" }).click();
  await page.getByRole("menuitem", { name: destination }).click();
}

test.describe("Atendimento", () => {
  test.beforeEach(async ({ page }) => {
    await installModuleSession(page, [
      {
        capabilities: [
          { name: "service_desk.ticket.move" },
          { name: "service_desk.ticket.read_queue" },
        ],
        companyId: alfaCompanyId,
        companyName: "Alfa",
        membershipId: alfaMembershipId,
      },
    ]);

    await page.route("**/atendimento/cards*", async (route) => {
      const request = route.request();
      expect(request.method()).toBe("GET");
      const url = new URL(request.url());
      expect(url.searchParams.get("companyId")).toBe(alfaCompanyId);
      await route.fulfill({
        body: JSON.stringify([cardJson()]),
        contentType: "application/json",
        status: 200,
      });
    });

    await page.route("**/atendimento/lookups*", async (route) => {
      await route.fulfill({
        body: JSON.stringify({ categories: [], channels: [] }),
        contentType: "application/json",
        status: 200,
      });
    });

    await page.route(
      `**/foundation/companies/${alfaCompanyId}/directory-options`,
      async (route) => {
        await route.fulfill({
          body: JSON.stringify([]),
          contentType: "application/json",
          status: 200,
        });
      },
    );
  });

  test("advances a card to the next column through the status menu", async ({
    page,
  }) => {
    const moveCalls: { column: string; expectedVersion: number }[] = [];

    await page.route("**/atendimento/cards/*/move", async (route) => {
      const request = route.request();
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      const payload = request.postDataJSON();
      expect(payload.requestId).toMatch(canonicalRequestIdPattern);
      moveCalls.push({
        column: payload.column,
        expectedVersion: payload.expectedVersion,
      });
      await route.fulfill({
        body: JSON.stringify(cardJson({ column: "em_andamento", version: 2 })),
        contentType: "application/json",
        status: 200,
      });
    });

    await openModulePage(page, "/atendimento");
    await expect(
      columnBoard(page, "Novos").getByText("Cobrança indevida"),
    ).toBeVisible();

    await moveTo(page, "Em andamento");

    await expect(
      columnBoard(page, "Em andamento").getByText("Cobrança indevida"),
    ).toBeVisible();
    await expect(
      columnBoard(page, "Novos").getByText("Nenhum cartão nesta coluna."),
    ).toBeVisible();
    expect(moveCalls).toEqual([{ column: "em_andamento", expectedVersion: 1 }]);
  });

  test("recovers from a 409 integrity conflict by syncing the card and retrying with the refreshed version", async ({
    page,
  }) => {
    const movePayloads: { column: string; expectedVersion: number }[] = [];

    await page.route("**/atendimento/cards/*/move", async (route) => {
      const request = route.request();
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      const payload = request.postDataJSON();
      movePayloads.push({
        column: payload.column,
        expectedVersion: payload.expectedVersion,
      });
      if (movePayloads.length === 1) {
        // Outra pessoa atualizou o cartão enquanto este operador atuava.
        await route.fulfill({
          body: JSON.stringify({
            code: "integrity_conflict",
            current: cardJson({ version: 2 }),
          }),
          contentType: "application/json",
          status: 409,
        });
        return;
      }
      await route.fulfill({
        body: JSON.stringify(cardJson({ column: "em_andamento", version: 3 })),
        contentType: "application/json",
        status: 200,
      });
    });

    await openModulePage(page, "/atendimento");

    await moveTo(page, "Em andamento");
    await expect(
      page.getByText(
        "O cartão foi atualizado enquanto você atuava. Verifique o estado atual da fila.",
      ),
    ).toBeVisible();
    await expect(
      columnBoard(page, "Novos").getByText("Cobrança indevida"),
    ).toBeVisible();

    await moveTo(page, "Em andamento");

    await expect(
      columnBoard(page, "Em andamento").getByText("Cobrança indevida"),
    ).toBeVisible();
    expect(movePayloads).toEqual([
      { column: "em_andamento", expectedVersion: 1 },
      { column: "em_andamento", expectedVersion: 2 },
    ]);
  });
});
