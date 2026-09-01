import { expect, type Page, test } from "@playwright/test";
import { signIn } from "./support/bff-session";
import {
  alfaCompanyId,
  alfaMembershipId,
  canonicalRequestIdPattern,
  csrfToken,
  installModuleSession,
  openModulePage,
} from "./support/module-session";

const createdDraftId = "71111111-1111-4111-8111-111111111111";
const confirmedDraftId = "72222222-2222-4222-8222-222222222222";
const proposalId = "e1111111-1111-4111-8111-111111111111";
const proposalInvoiceFileId = "61111111-1111-4111-8111-111111111111";

type DraftOverrides = Partial<ReturnType<typeof draftJson>>;

function draftJson(overrides: DraftOverrides = {}) {
  return {
    amount: "1250.00",
    beneficiaryDisplayName: "Distribuidora Luz SA",
    companyId: alfaCompanyId,
    createdAt: "2026-09-01T12:00:00+00:00",
    currency: "BRL",
    description: "Fatura de agosto da distribuidora",
    dueDate: "2026-12-31",
    id: createdDraftId,
    invoiceFileId: null,
    proofRequirement: "optional",
    requestId: "73333333-3333-4333-8333-333333333333",
    state: "rascunho",
    title: "Energia elétrica de agosto",
    version: 1,
    ...overrides,
  };
}

function proposalJson() {
  return {
    amount: "2345.67",
    beneficiaryDisplayName: "Companhia Aérea Aurora",
    companyId: alfaCompanyId,
    confidence: 0.92,
    createdAt: "2026-09-01T09:00:00+00:00",
    currency: "BRL",
    description: "Reembolso de viagem comercial",
    dueDate: "2026-10-15",
    expiresAt: "2026-09-30T09:00:00+00:00",
    id: proposalId,
    invoiceFileId: proposalInvoiceFileId,
    proofRequirement: "optional",
    sourceReferences: [{ field: "amount", source: "anexo.pdf" }],
    state: "pending",
    title: "Reembolso de viagem",
  };
}

async function installPaguaiBff(page: Page) {
  const draftsList: ReturnType<typeof draftJson>[] = [];
  const createdDrafts: unknown[] = [];
  const confirmedProposals: { payload: unknown; url: string }[] = [];
  const proposalReads: string[] = [];

  await installModuleSession(page, [
    {
      capabilities: [
        { name: "paguai.draft.create" },
        { name: "paguai.draft.read" },
      ],
      companyId: alfaCompanyId,
      companyName: "Alfa",
      membershipId: alfaMembershipId,
    },
  ]);

  await page.route("**/paguai/requests*", async (route) => {
    const request = route.request();
    if (request.method() === "POST") {
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      const payload = request.postDataJSON();
      createdDrafts.push(payload);
      const draft = draftJson({
        amount: payload.amount,
        beneficiaryDisplayName: payload.beneficiaryDisplayName,
        description: payload.description,
        dueDate: payload.dueDate,
        proofRequirement: payload.proofRequirement,
        title: payload.title,
      });
      draftsList.push(draft);
      await route.fulfill({
        body: JSON.stringify(draft),
        contentType: "application/json",
        status: 201,
      });
      return;
    }
    await route.fulfill({
      body: JSON.stringify(draftsList),
      contentType: "application/json",
      status: 200,
    });
  });

  await page.route(
    `**/paguai/draft-proposals/${proposalId}*`,
    async (route) => {
      const url = new URL(route.request().url());
      proposalReads.push(url.searchParams.get("companyId") ?? "");
      await route.fulfill({
        body: JSON.stringify(proposalJson()),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  await page.route(
    `**/paguai/draft-proposals/${proposalId}/confirm`,
    async (route) => {
      const request = route.request();
      expect(request.method()).toBe("POST");
      expect(request.headers()["x-csrf-token"]).toBe(csrfToken);
      confirmedProposals.push({
        payload: request.postDataJSON(),
        url: request.url(),
      });
      await route.fulfill({
        body: JSON.stringify(
          draftJson({
            amount: "2345.67",
            beneficiaryDisplayName: "Companhia Aérea Aurora",
            description: "Reembolso de viagem comercial",
            dueDate: "2026-10-15",
            id: confirmedDraftId,
            title: "Reembolso de viagem",
          }),
        ),
        contentType: "application/json",
        status: 200,
      });
    },
  );

  return { confirmedProposals, createdDrafts, draftsList, proposalReads };
}

test.describe("Paguai", () => {
  test("creates a draft from a pt-BR amount and lists it as a request", async ({
    page,
  }) => {
    const { createdDrafts } = await installPaguaiBff(page);

    await openModulePage(page, "/paguai");
    await expect(
      page.getByText("Nenhuma solicitação encontrada."),
    ).toBeVisible();

    await page.getByRole("button", { name: "Nova solicitação" }).click();
    const createDialog = page.getByRole("dialog", {
      name: "Nova solicitação",
    });
    await expect(createDialog).toBeVisible();

    await createDialog.getByLabel("Título").fill("Energia elétrica de agosto");
    await createDialog.getByLabel("Valor (R$)").fill("1.250,00");
    await createDialog.getByLabel("Pagar até").fill("2026-12-31");
    await createDialog.getByLabel("Quem receberá").fill("Distribuidora Luz SA");
    await createDialog.getByLabel("Comprovante de pagamento").click();
    await page.getByRole("option", { name: "Opcional" }).click();
    await createDialog
      .getByLabel("Descrição")
      .fill("Fatura de agosto da distribuidora");
    await createDialog.getByRole("button", { name: "Criar rascunho" }).click();

    await expect(createDialog).not.toBeVisible();
    const createdCard = page
      .locator(".MuiCard-root")
      .filter({ hasText: "Energia elétrica de agosto" });
    await expect(createdCard.getByText("R$ 1.250,00")).toBeVisible();
    await expect(createdCard.getByText("Pagar até: 31/12/2026")).toBeVisible();
    await expect(createdCard.getByText("Rascunho")).toBeVisible();

    expect(createdDrafts).toHaveLength(1);
    expect(createdDrafts[0]).toEqual({
      amount: "1250.00",
      beneficiaryDisplayName: "Distribuidora Luz SA",
      companyId: alfaCompanyId,
      currency: "BRL",
      description: "Fatura de agosto da distribuidora",
      dueDate: "2026-12-31",
      invoiceFileId: null,
      proofRequirement: "optional",
      requestId: expect.stringMatching(canonicalRequestIdPattern),
      title: "Energia elétrica de agosto",
    });
  });

  test("confirms a reviewed proposal and moves it into the requests list", async ({
    page,
  }) => {
    const { confirmedProposals, proposalReads } = await installPaguaiBff(page);

    await page.goto(`/paguai?proposal=${proposalId}`);
    await expect(page).toHaveURL(/\/login$/);
    await signIn(page);
    // O retorno do login não preserva caminhos com query: autentica na home
    // e então navega ao deep link da proposta com a sessão válida.
    await expect(page).toHaveURL(/\/$/);
    await page.goto(`/paguai?proposal=${proposalId}`);
    await expect(page).toHaveURL(
      new RegExp(`/paguai\\?proposal=${proposalId}$`),
    );

    const reviewDialog = page.getByRole("dialog", { name: "Revisar proposta" });
    await expect(reviewDialog).toBeVisible();
    await expect(
      reviewDialog.getByText("Reembolso de viagem", { exact: true }),
    ).toBeVisible();
    await expect(reviewDialog.getByText("R$ 2.345,67")).toBeVisible();
    await expect(reviewDialog.getByText("Pagar até: 15/10/2026")).toBeVisible();
    expect(proposalReads).toEqual([alfaCompanyId]);

    await reviewDialog
      .getByRole("button", { name: "Confirmar solicitação" })
      .click();

    await expect(
      page.getByText("Solicitação criada a partir da proposta."),
    ).toBeVisible();
    await expect(reviewDialog).not.toBeVisible();
    await expect(page).toHaveURL(/\/paguai$/);

    const confirmedCard = page
      .locator(".MuiCard-root")
      .filter({ hasText: "Reembolso de viagem" });
    await expect(confirmedCard.getByText("R$ 2.345,67")).toBeVisible();
    await expect(confirmedCard.getByText("Rascunho")).toBeVisible();

    expect(confirmedProposals).toHaveLength(1);
    expect(confirmedProposals[0].url).toMatch(
      new RegExp(`/paguai/draft-proposals/${proposalId}/confirm$`),
    );
    expect(confirmedProposals[0].payload).toEqual({
      companyId: alfaCompanyId,
      requestId: expect.stringMatching(canonicalRequestIdPattern),
    });
  });
});
