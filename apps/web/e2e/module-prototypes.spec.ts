import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, test } from "@playwright/test";
import { installBffSession, signIn } from "./support/bff-session";

test.beforeEach(async ({ page }) => {
  await installBffSession(page);
});

async function enterHome(page: Page) {
  await page.goto("/login");
  await signIn(page);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("heading", { name: "Início" })).toBeVisible();
}

async function openPaguaiPrototypeFromHome(page: Page) {
  await enterHome(page);
  await page.getByRole("link", { name: "Abrir protótipo Paguai" }).click();
  await expect(page).toHaveURL(/\/prototipos\/paguai$/);
  await expect(page.getByRole("heading", { name: "Paguai" })).toBeVisible();
}

test("Scenario: development sign-in shows prototype notice, index and Home Paguai action", async ({
  page,
}) => {
  await enterHome(page);

  const sidebar = page.getByRole("navigation", { name: "Navegação lateral" });
  await sidebar.getByRole("link", { name: "Demonstração" }).click();
  await expect(page).toHaveURL(/\/prototipos$/);
  await expect(
    page.getByRole("heading", { name: "Protótipos planejados" }),
  ).toBeVisible();
  await expect(page.getByRole("alert")).toContainText("Dados de demonstração");

  await sidebar.getByRole("link", { name: "Início" }).click();
  await expect(page).toHaveURL(/\/$/);
  await page.getByRole("link", { name: "Abrir protótipo Paguai" }).click();
  await expect(page).toHaveURL(/\/prototipos\/paguai$/);
  await expect(page.getByRole("alert")).toContainText("Dados de demonstração");
});

test("Scenario: prototype draft requires a company and uses MUI company option", async ({
  page,
}) => {
  await openPaguaiPrototypeFromHome(page);

  await page.getByRole("button", { name: "Nova solicitação" }).click();
  const dialog = page.getByRole("dialog", { name: "Nova solicitação" });
  await dialog.getByRole("button", { name: "Salvar rascunho" }).click();
  const company = dialog.getByRole("combobox", { name: "Empresa" });
  await expect(company).toBeFocused();
  await expect(dialog.getByText("Selecione uma empresa")).toBeVisible();

  await company.click();
  await page.getByRole("option", { name: "Alfa", exact: true }).click();
  await dialog.getByLabel("Descrição").fill("Licença anual");
  await dialog.getByLabel("Valor").fill("1200");
  await dialog.getByRole("button", { name: "Salvar rascunho" }).click();

  await expect(page.getByText("Licença anual", { exact: true })).toBeVisible();
  await expect(page.getByRole("status")).toContainText(
    "Alteração local de demonstração",
  );
});

test("Scenario: conference validates then keeps return reason and focus", async ({
  page,
}) => {
  await openPaguaiPrototypeFromHome(page);
  await page.getByRole("tab", { name: "Conferência" }).click();
  await page
    .getByRole("button", {
      name: "Selecionar Consultoria tributária da empresa Beta",
    })
    .click();
  const selectedRequest = page.getByRole("region", {
    name: "Solicitação selecionada",
  });
  await selectedRequest
    .getByRole("button", {
      name: "Devolver Consultoria tributária para ajuste",
    })
    .click();
  const dialog = page.getByRole("dialog", { name: "Devolver solicitação" });
  await dialog.getByRole("button", { name: "Confirmar devolução" }).click();
  const category = dialog.getByRole("combobox", { name: "Categoria" });
  await expect(category).toBeFocused();
  await expect(dialog.getByText("Selecione uma categoria")).toBeVisible();

  await category.click();
  await page
    .getByRole("option", { name: "Dados incorretos", exact: true })
    .click();
  await dialog
    .getByLabel("Descrição da devolução")
    .fill("Anexe a nota fiscal correta");
  await dialog.getByRole("button", { name: "Confirmar devolução" }).click();

  await expect(selectedRequest).toContainText("Categoria: Dados incorretos");
  await expect(selectedRequest).toContainText(
    "Motivo: Anexe a nota fiscal correta",
  );
  await expect(selectedRequest).toBeFocused();
});

test("Scenario: finance blocks payment until proof ready", async ({ page }) => {
  await openPaguaiPrototypeFromHome(page);
  await page.getByRole("tab", { name: "Financeiro" }).click();
  const confirm = page.getByRole("button", { name: "Confirmar pagamento" });
  await expect(confirm).toBeDisabled();

  await page
    .getByRole("button", { name: "Adicionar arquivo de demonstração" })
    .click();
  await page
    .getByRole("button", { name: "Avançar estado demonstrativo" })
    .click();
  await expect(page.getByText("Estado demonstrativo: ready")).toBeVisible();
  await expect(confirm).toBeEnabled();
  await confirm.click();
  await expect(page.getByRole("status")).toContainText(
    "Pagamento confirmado localmente",
  );
});

test("Scenario: theme, keyboard, narrow viewport and axe remain usable", async ({
  page,
}) => {
  await openPaguaiPrototypeFromHome(page);
  const appearanceTrigger = page.getByRole("button", {
    name: "Abrir preferências visuais",
  });
  await appearanceTrigger.click();
  await page.getByRole("menuitemradio", { name: "Tema: claro" }).click();
  await appearanceTrigger.click();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: claro" }),
  ).toHaveAttribute("aria-checked", "true");
  await page.getByRole("menuitemradio", { name: "Tema: escuro" }).click();
  await appearanceTrigger.click();
  await expect(
    page.getByRole("menuitemradio", { name: "Tema: escuro" }),
  ).toHaveAttribute("aria-checked", "true");
  await page.keyboard.press("Escape");

  const newRequest = page.getByRole("button", { name: "Nova solicitação" });
  await newRequest.focus();
  await newRequest.press("Enter");
  await expect(
    page.getByRole("dialog", { name: "Nova solicitação" }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(newRequest).toBeFocused();

  await page.setViewportSize({ height: 844, width: 320 });
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    )
    .toBe(true);

  await page.emulateMedia({ forcedColors: "active" });
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();
  expect(results.violations).toEqual([]);
});

test("Scenario: agent dock runs a typed agent run and shares the conversation with the dedicated page", async ({
  page,
}) => {
  await enterHome(page);

  // O chat consulta o POST /agent/runs real: a rota é interceptada com a
  // resposta tipada do contrato (antiforgery já vem do installBffSession).
  let runRequest: { body: unknown; headers: Record<string, string> } | null =
    null;
  await page.route("**/agent/runs", async (route) => {
    const request = route.request();
    runRequest = {
      body: request.postDataJSON(),
      headers: request.headers(),
    };
    await route.fulfill({
      body: JSON.stringify({
        correlationId: "0192b0c0-0000-7000-8000-000000000001",
        reasonCode: "ok",
        sources: [{ reference: "Solicitação Paguai nº 123" }],
        status: "result",
        text: "Resposta autorizada pelo gate",
      }),
      contentType: "application/json",
      status: 200,
    });
  });

  await page.getByRole("button", { name: "Abrir painel do agente" }).click();
  const dock = page.getByRole("complementary", { name: "Painel do agente" });
  await expect(dock).toBeVisible();

  await dock
    .getByLabel("Mensagem para o agente")
    .fill("Preciso antecipar um pagamento");
  await dock.getByRole("button", { name: "Enviar" }).click();
  await expect(dock.getByText("Preciso antecipar um pagamento")).toBeVisible();
  await expect(dock.getByText("Resposta autorizada pelo gate")).toBeVisible();
  await expect(dock.getByText("Solicitação Paguai nº 123")).toBeVisible();
  // A ação proposta permanece opaca: nenhum botão de execução existe.
  await expect(
    dock.getByRole("button", { name: "Aprovar execução" }),
  ).toHaveCount(0);

  expect(runRequest).not.toBeNull();
  const capturedRun = runRequest as {
    body: { requestId?: unknown };
    headers: Record<string, string>;
  };
  expect(capturedRun.body).toMatchObject({
    companyId: "33333333-3333-4333-8333-333333333333",
    journey: "read_only_summarize_v1",
    message: "Preciso antecipar um pagamento",
  });
  expect(capturedRun.body.requestId).toEqual(
    expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    ),
  );
  expect(capturedRun.headers["x-csrf-token"]).toBe("test-csrf-token");

  await dock
    .getByRole("button", { name: "Abrir página dedicada do agente" })
    .click();
  await expect(page).toHaveURL(/\/agente$/);
  await expect(page.getByRole("heading", { name: "Agente" })).toBeVisible();
  await expect(page.getByText("Preciso antecipar um pagamento")).toBeVisible();

  await page
    .getByRole("button", { name: "Voltar ao painel do agente" })
    .click();
  await expect(page).toHaveURL(/\/$/);
  await expect(
    page.getByRole("complementary", { name: "Painel do agente" }),
  ).toBeVisible();
  await expect(page.getByText("Preciso antecipar um pagamento")).toBeVisible();

  const agentScan = await new AxeBuilder({ page })
    .include("#vinx-agent-dock")
    .analyze();
  expect(agentScan.violations).toEqual([]);
});

test("Scenario: agent chat uploads, removes, and sends the remaining attachment in order", async ({
  page,
}) => {
  await enterHome(page);

  const fileIds = [
    "44444444-4444-4444-8444-444444444444",
    "55555555-5555-4555-8555-555555555555",
  ];
  const confirmationIds: string[] = [];
  let antiforgeryRequests = 0;
  let intentIndex = 0;
  let storagePuts = 0;
  let runBody: { attachments?: string[] } | null = null;
  await page.route(
    "**/foundation/identity/browser-session/antiforgery",
    async (route) => {
      antiforgeryRequests += 1;
      await route.fulfill({
        body: JSON.stringify({ requestToken: "test-csrf-token" }),
        contentType: "application/json",
        status: 200,
      });
    },
  );
  await page.route("**/foundation/files/upload-intentions", async (route) => {
    expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
    const fileId = fileIds[intentIndex++];
    await route.fulfill({
      body: JSON.stringify({
        fileId,
        processingState: "quarantined",
        uploadUrl: `https://storage.test.invalid/${fileId}`,
      }),
      contentType: "application/json",
      status: 200,
    });
  });
  await page.route("https://storage.test.invalid/**", async (route) => {
    expect(route.request().method()).toBe("PUT");
    expect(route.request().headers()["content-type"]).toBe("application/pdf");
    storagePuts += 1;
    await route.fulfill({ status: 204 });
  });
  await page.route("**/foundation/files/confirmations", async (route) => {
    expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf-token");
    const body = route.request().postDataJSON() as { fileId: string };
    confirmationIds.push(body.fileId);
    await route.fulfill({
      body: JSON.stringify({
        fileId: body.fileId,
        processingState: "confirmed",
      }),
      contentType: "application/json",
      status: 200,
    });
  });
  await page.route("**/agent/runs", async (route) => {
    runBody = route.request().postDataJSON() as { attachments?: string[] };
    await route.fulfill({
      body: JSON.stringify({
        correlationId: "0192b0c0-0000-7000-8000-000000000001",
        reasonCode: "ok",
        sources: null,
        status: "result",
        text: "Resposta autorizada pelo gate",
      }),
      contentType: "application/json",
      status: 200,
    });
  });

  await page.getByRole("button", { name: "Abrir painel do agente" }).click();
  const dock = page.getByRole("complementary", { name: "Painel do agente" });
  await dock.getByLabel("Anexar documentos ao agente").setInputFiles([
    {
      buffer: Buffer.from([1]),
      mimeType: "application/pdf",
      name: "primeiro.pdf",
    },
    {
      buffer: Buffer.from([2]),
      mimeType: "application/pdf",
      name: "segundo.pdf",
    },
  ]);
  await expect(dock.getByText("Pronto")).toHaveCount(2);
  await dock.getByRole("button", { name: "Remover primeiro.pdf" }).click();
  await expect(dock.getByText("primeiro.pdf")).toHaveCount(0);

  await dock
    .getByLabel("Mensagem para o agente")
    .fill("Resuma o documento restante");
  await dock.getByRole("button", { name: "Enviar" }).click();
  await expect(dock.getByText("Resposta autorizada pelo gate")).toBeVisible();

  expect(antiforgeryRequests).toBe(5);
  expect(storagePuts).toBe(2);
  expect(confirmationIds).toEqual(fileIds);
  expect(runBody?.attachments).toEqual([fileIds[1]]);
});
