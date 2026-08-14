# Planned Module Prototypes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add development-only, clearly labeled, functional UI prototypes for Paguai, Kanban, Atendimento, and Pessoas using feature-owned state and reusable presentational patterns, delivered as two reviewable PRs.

**Architecture:** A development-only prototype route manifest plugs into the app router and navigation without changing production destinations. Each feature owns fixtures, types, and a reducer/hook; shared prototype components cover only currently repeated UI patterns such as the notice, detail drawer, status badge, and demonstrative attachment field.

**Tech Stack:** React 19, TypeScript 6, React Router 7, MUI 9.3.1, React Hook Form 7, Zod 4, Vitest, Testing Library, Playwright.

## Global Constraints

- PR 2 creates `codex/ui-paguai-kanban-prototypes` from the reviewed `codex/ui-workspace-shell` head.
- PR 3 creates `codex/ui-atendimento-pessoas-prototypes` from the reviewed `codex/ui-paguai-kanban-prototypes` head.
- Prototype routes and their navigation group render only when `import.meta.env.DEV` is true.
- Every prototype page visibly says `Dados de demonstração` and never claims API persistence.
- State is feature-owned, local, and reset by page reload.
- Do not add fake data to normal production routes or the all-authorized Home.
- Do not add command palette, standalone Files route, CRM, business chat, PWA, offline cache, queue, or replay.
- Documents appear only inside Paguai, Atendimento, and Pessoas flows.
- Keyboard controls must cover every action; Kanban cannot require drag-and-drop.
- Do not add a dependency.
- Never access, list, or execute `quality/private`.

---

## File Structure

### PR 2: laboratory, Paguai, and Kanban (maximum 25 files)

**Create shared prototype files**

- `apps/web/src/features/prototypes/prototype-index-page.tsx` — index linking documented prototype families.
- `apps/web/src/features/prototypes/prototype-notice.tsx` — persistent truthful demo label.
- `apps/web/src/features/prototypes/detail-drawer.tsx` — accessible feature detail frame.
- `apps/web/src/features/prototypes/demo-attachment-field.tsx` — local upload-state demonstrator.
- `apps/web/src/features/prototypes/prototype-index-page.test.tsx`

**Create Paguai files**

- `apps/web/src/features/paguai/prototype/paguai-prototype-types.ts`
- `apps/web/src/features/paguai/prototype/paguai-prototype-fixtures.ts`
- `apps/web/src/features/paguai/prototype/use-paguai-prototype.ts`
- `apps/web/src/features/paguai/prototype/paguai-prototype-page.tsx`
- `apps/web/src/features/paguai/prototype/paguai-prototype-page.test.tsx`

**Create Kanban files**

- `apps/web/src/features/kanban/prototype/kanban-prototype-types.ts`
- `apps/web/src/features/kanban/prototype/kanban-prototype-fixtures.ts`
- `apps/web/src/features/kanban/prototype/use-kanban-prototype.ts`
- `apps/web/src/features/kanban/prototype/kanban-prototype-page.tsx`
- `apps/web/src/features/kanban/prototype/kanban-prototype-page.test.tsx`

**Create PR 2 browser proof**

- `apps/web/playwright.prototypes.config.ts` — isolated Vite development server on port 4174.
- `apps/web/e2e/module-prototypes.spec.ts`
- `docs/verification/frontend-paguai-kanban-prototypes-test-cases.md`

**Modify in PR 2**

- `apps/web/package.json`
- `apps/web/src/shared/routing/app-paths.ts`
- `apps/web/src/app/app-route-manifest.tsx`
- `apps/web/src/app/router.test.tsx`
- `apps/web/src/app/home-page.tsx`
- `apps/web/src/app/app-shell.test.tsx`
- `apps/web/e2e/app-shell-home.spec.ts`

### PR 3: Atendimento and Pessoas (maximum 20 files)

**Create Atendimento files**

- `apps/web/src/features/service-desk/prototype/service-desk-prototype-types.ts`
- `apps/web/src/features/service-desk/prototype/service-desk-prototype-fixtures.ts`
- `apps/web/src/features/service-desk/prototype/use-service-desk-prototype.ts`
- `apps/web/src/features/service-desk/prototype/service-desk-prototype-page.tsx`
- `apps/web/src/features/service-desk/prototype/service-desk-prototype-page.test.tsx`

**Create Pessoas files**

- `apps/web/src/features/people/prototype/people-prototype-types.ts`
- `apps/web/src/features/people/prototype/people-prototype-fixtures.ts`
- `apps/web/src/features/people/prototype/use-people-prototype.ts`
- `apps/web/src/features/people/prototype/people-prototype-page.tsx`
- `apps/web/src/features/people/prototype/people-prototype-page.test.tsx`

**Create PR 3 browser proof**

- `docs/verification/frontend-atendimento-pessoas-prototypes-test-cases.md`

**Modify in PR 3**

- `apps/web/src/features/prototypes/prototype-index-page.tsx`
- `apps/web/src/features/prototypes/prototype-index-page.test.tsx`
- `apps/web/src/shared/routing/app-paths.ts`
- `apps/web/src/app/app-route-manifest.tsx`
- `apps/web/src/app/router.test.tsx`
- `apps/web/src/app/home-page.tsx`
- `apps/web/src/app/app-shell.test.tsx`
- `apps/web/e2e/module-prototypes.spec.ts`

### Task 1: Development-only prototype route family and shared frames

**Files:**

- Create: all five files under `apps/web/src/features/prototypes/`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`
- Modify: `apps/web/src/app/router.test.tsx`

**Interfaces:**

- Produces: `prototypeCatalog`, development-gated app route definitions, `PrototypeNotice`, `DetailDrawer`, `DemoAttachmentField`.
- `DetailDrawer` props: `{ open, title, onClose, children, actions? }`.
- `DemoAttachmentField` props: `{ label, required, value, onChange }` where `value` is `"empty" | "processing" | "ready" | "failed"`.

- [ ] **Step 1: Write failing route and index tests**

```tsx
it("exposes the prototype index only in development", async () => {
  const router = renderRoute("/prototipos", await signedInGateway());
  expect(await screen.findByRole("heading", { name: "Protótipos planejados" })).toBeVisible();
  expect(screen.getByText("Dados de demonstração")).toBeVisible();
  expect(router.state.location.pathname).toBe("/prototipos");
});

it.each(["Paguai", "Kanban", "Atendimento", "Pessoas"])(
  "lists the planned %s surface honestly",
  async (name) => {
    render(<PrototypeIndexPage />);
    expect(screen.getByRole("heading", { name })).toBeVisible();
    expect(screen.getByText(`${name}: em preparação nesta série`)).toBeVisible();
  },
);

it("returns no prototype route definitions when disabled", () => {
  expect(selectPrototypeRoutes(false)).toEqual([]);
});
```

- [ ] **Step 2: Run and observe missing routes/components**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/features/prototypes/prototype-index-page.test.tsx
```

Expected: FAIL because `/prototipos` and shared components do not exist.

- [ ] **Step 3: Implement the truthful index and notice**

```tsx
export function PrototypeNotice() {
  return (
    <Alert icon={<ScienceOutlinedIcon />} severity="warning" variant="outlined">
      <AlertTitle>Dados de demonstração</AlertTitle>
      As alterações ficam somente nesta tela e são descartadas ao recarregar.
    </Alert>
  );
}
```

```tsx
const prototypeCards = [
  { id: "paguai", label: "Paguai", path: "/prototipos/paguai", description: "Solicitações, conferência, financeiro e políticas", available: false },
  { id: "kanban", label: "Kanban", path: "/prototipos/kanban", description: "Quadro, detalhe, movimento e histórico", available: false },
  { id: "atendimento", label: "Atendimento", path: "/prototipos/atendimento", description: "Fila, ticket, timeline e SLA", available: false },
  { id: "pessoas", label: "Pessoas", path: "/prototipos/pessoas", description: "Diretório, ausências, RH e equipe", available: false },
] as const;
```

Available cards render a link. Unavailable cards render the exact visible text
`{label}: em preparação nesta série` and no link to a missing route. Tasks 2,
3, 5, and 6 flip availability only when the corresponding page exists.

- [ ] **Step 4: Implement the two shared overlays**

`DetailDrawer` uses MUI directly and restores focus through its modal behavior:

```tsx
export function DetailDrawer({ open, title, onClose, children, actions }: DetailDrawerProps) {
  return (
    <Drawer anchor="right" onClose={onClose} open={open}>
      <Box sx={{ maxWidth: "100vw", p: 3, width: { xs: "100vw", sm: 480 } }}>
        <Stack alignItems="center" direction="row" gap={2}>
          <Typography component="h2" sx={{ flex: 1 }} variant="h6">{title}</Typography>
          <IconButton aria-label="Fechar detalhes" onClick={onClose}><CloseOutlinedIcon /></IconButton>
        </Stack>
        <Box sx={{ py: 3 }}>{children}</Box>
        {actions ? <Stack direction="row" gap={1}>{actions}</Stack> : null}
      </Box>
    </Drawer>
  );
}
```

`DemoAttachmentField` transitions only through explicit local buttons:

```tsx
const nextState = value === "empty" ? "processing" : value === "processing" ? "ready" : "empty";
<Button onClick={() => onChange(nextState)}>
  {value === "empty" ? "Adicionar arquivo de demonstração" : "Avançar estado demonstrativo"}
</Button>
```

- [ ] **Step 5: Register the development-only index route**

```tsx
const prototypeRoutes = [
  { path: "/prototipos", label: "Demonstração", group: "support", icon: ScienceOutlinedIcon, element: <PrototypeIndexPage />, navigation: true },
] as const satisfies readonly AppRouteDefinition[];

export function selectPrototypeRoutes(enabled: boolean) {
  return enabled ? prototypeRoutes : [];
}

export const appRouteManifest = [
  ...productRouteManifest,
  ...selectPrototypeRoutes(import.meta.env.DEV),
] as const;
```

Keep this composition in the existing app-layer manifest so router and both
navigations still consume one definition. Add `/prototipos` to `AppPath`; safe
return-path validation may recognize it, but the production manifest omits it
and the existing wildcard returns to `/`. Do not add every prototype to the
primary navigation; the index owns those links.

- [ ] **Step 6: Run focused tests and commit**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/features/prototypes/prototype-index-page.test.tsx
git add apps/web/src/features/prototypes apps/web/src/shared/routing/app-paths.ts apps/web/src/app/app-route-manifest.tsx apps/web/src/app/router.test.tsx
git commit -m "feat(web): add development prototype workspace"
```

Expected: tests PASS and the normal production route manifest remains unchanged.

### Task 2: Functional Paguai prototype

**Files:**

- Create: all five files under `apps/web/src/features/paguai/prototype/`
- Modify: `apps/web/src/features/prototypes/prototype-index-page.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`

**Interfaces:**

- `PaguaiSection = "requests" | "conference" | "finance" | "policies"`.
- `PaguaiRequest` includes `id`, `company`, `status`, `amount`, `dueDate`, `invoiceState`, `proofRequired`.
- `usePaguaiPrototype()` returns `{ requests, selected, section, filters, actions }` with local-only actions.

- [ ] **Step 1: Write failing interaction tests**

```tsx
it("filters requests and creates a local draft with explicit company", async () => {
  const user = userEvent.setup();
  render(<PaguaiPrototypePage />);

  await user.click(screen.getByRole("button", { name: "Nova solicitação" }));
  await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));
  expect(screen.getByText("Selecione uma empresa")).toBeVisible();

  await user.click(screen.getByRole("combobox", { name: "Empresa" }));
  await user.click(screen.getByRole("option", { name: "Alfa" }));
  await user.type(screen.getByLabelText("Descrição"), "Licença anual");
  await user.type(screen.getByLabelText("Valor"), "1200");
  await user.click(screen.getByRole("button", { name: "Salvar rascunho" }));

  expect(screen.getByText("Licença anual")).toBeVisible();
  expect(screen.getByText("Alteração local de demonstração")).toBeVisible();
});
```

Add tests that Conference opens a return dialog requiring category/description
and Finance refuses the local confirmation while proof state is not `ready`.

- [ ] **Step 2: Run and observe missing feature**

```bash
npm --prefix apps/web test -- --run src/features/paguai/prototype/paguai-prototype-page.test.tsx
```

Expected: FAIL because the feature files do not exist.

- [ ] **Step 3: Define concrete types, fixtures, and reducer**

```ts
export type PaguaiRequest = {
  id: string;
  description: string;
  company: "alfa" | "beta";
  status: "draft" | "conference" | "approval" | "finance" | "paid" | "returned";
  amount: number;
  dueDate: string;
  invoiceState: "metadata" | "authorized";
  proofRequired: boolean;
};

type PaguaiAction =
  | { type: "createDraft"; request: PaguaiRequest }
  | { type: "returnRequest"; id: string; reason: string }
  | { type: "confirmPayment"; id: string }
  | { type: "select"; id: string | null }
  | { type: "setSection"; section: PaguaiSection };
```

The reducer updates immutable arrays and appends `demoEvents`; it never uses a
timer or claims remote persistence.

- [ ] **Step 4: Compose the four documented Paguai surfaces**

Use MUI `Tabs` with exact labels `Solicitações`, `Conferência`, `Financeiro`,
and `Políticas`. The request list uses company/status filters and opens
`DetailDrawer`. Forms use React Hook Form + Zod:

```ts
const draftSchema = z.object({
  company: z.enum(["alfa", "beta"], { error: "Selecione uma empresa" }),
  description: z.string().trim().min(3, "Descreva a solicitação"),
  amount: z.coerce.number().positive("Informe um valor maior que zero"),
});
```

The Finance confirmation renders `DemoAttachmentField` and disables confirm
when `proofRequired && proofState !== "ready"`. The policy tab edits one local
audience selector and displays `Simulação sem efeito real`.

Wire MUI selects explicitly so label queries and assistive technology resolve
the same control:

```tsx
<Controller
  control={control}
  name="company"
  render={({ field, fieldState }) => (
    <FormControl error={Boolean(fieldState.error)} fullWidth>
      <InputLabel id="paguai-company-label">Empresa</InputLabel>
      <Select
        {...field}
        inputProps={{ "aria-describedby": fieldState.error ? "paguai-company-error" : undefined }}
        label="Empresa"
        labelId="paguai-company-label"
      >
        <MenuItem value="alfa">Alfa</MenuItem>
        <MenuItem value="beta">Beta</MenuItem>
      </Select>
      {fieldState.error ? <FormHelperText id="paguai-company-error">{fieldState.error.message}</FormHelperText> : null}
    </FormControl>
  )}
/>
```

After the page exists, append `/prototipos/paguai` as a non-navigation route in
the app manifest, add it to `AppPath`, and set only Paguai's catalog item to
`available: true`.

- [ ] **Step 5: Run tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/paguai/prototype/paguai-prototype-page.test.tsx
git add apps/web/src/features/paguai/prototype apps/web/src/features/prototypes/prototype-index-page.tsx apps/web/src/shared/routing/app-paths.ts apps/web/src/app/app-route-manifest.tsx
git commit -m "feat(web): add functional Paguai prototype"
```

Expected: PASS.

### Task 3: Accessible Kanban prototype

**Files:**

- Create: all five files under `apps/web/src/features/kanban/prototype/`
- Modify: `apps/web/src/features/prototypes/prototype-index-page.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`

**Interfaces:**

- `KanbanColumn = { id, title }`.
- `KanbanCard = { id, title, company, columnId, version, readOnlyProjection }`.
- `moveCard(cardId, destinationId)` updates one local card and appends one history event unless `readOnlyProjection`.

- [ ] **Step 1: Write failing movement tests**

```tsx
it("moves a normal card with a keyboard-operable dialog", async () => {
  const user = userEvent.setup();
  render(<KanbanPrototypePage />);

  await user.click(screen.getByRole("button", { name: "Abrir card Preparar proposta" }));
  await user.click(screen.getByRole("button", { name: "Mover card" }));
  await user.click(screen.getByRole("combobox", { name: "Coluna de destino" }));
  await user.click(screen.getByRole("option", { name: "Em andamento" }));
  await user.click(screen.getByRole("button", { name: "Confirmar movimento local" }));

  expect(screen.getByRole("region", { name: "Em andamento" })).toHaveTextContent("Preparar proposta");
  expect(screen.getByText(/movido.*Em andamento/i)).toBeVisible();
});

it("does not offer movement for a Paguai projection", async () => {
  render(<KanbanPrototypePage />);
  await userEvent.setup().click(screen.getByRole("button", { name: /Solicitação Paguai/ }));
  expect(screen.queryByRole("button", { name: "Mover card" })).not.toBeInTheDocument();
  expect(screen.getByText("Projeção somente leitura")).toBeVisible();
});
```

- [ ] **Step 2: Run and observe missing feature**

```bash
npm --prefix apps/web test -- --run src/features/kanban/prototype/kanban-prototype-page.test.tsx
```

Expected: FAIL.

- [ ] **Step 3: Implement local types, fixtures, and reducer**

```ts
export function moveCard(state: KanbanState, cardId: string, destinationId: string): KanbanState {
  const card = state.cards.find((candidate) => candidate.id === cardId);
  if (!card || card.readOnlyProjection || !state.columns.some((column) => column.id === destinationId)) {
    return state;
  }
  return {
    ...state,
    cards: state.cards.map((candidate) =>
      candidate.id === cardId
        ? { ...candidate, columnId: destinationId, version: candidate.version + 1 }
        : candidate,
    ),
    history: [...state.history, { cardId, destinationId, kind: "demo-move" }],
  };
}
```

- [ ] **Step 4: Render board, card drawer, and move dialog**

Each column is `role="region"` with its title. Cards are buttons, not draggable
divs. `DetailDrawer` presents description/history; a MUI `Dialog` selects the
destination and confirms the local move. The projection card links visually
back to Paguai but stays non-mutating.

After the page exists, append `/prototipos/kanban` as a non-navigation route,
add it to `AppPath`, and set only Kanban's catalog item to `available: true`.

- [ ] **Step 5: Run tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/kanban/prototype/kanban-prototype-page.test.tsx
git add apps/web/src/features/kanban/prototype apps/web/src/features/prototypes/prototype-index-page.tsx apps/web/src/shared/routing/app-paths.ts apps/web/src/app/app-route-manifest.tsx
git commit -m "feat(web): add accessible Kanban prototype"
```

Expected: PASS.

### Task 4: Prove PR 2 in development and production modes

**Files:**

- Create: `apps/web/playwright.prototypes.config.ts`
- Create: `apps/web/e2e/module-prototypes.spec.ts`
- Create: `docs/verification/frontend-paguai-kanban-prototypes-test-cases.md`
- Modify: `apps/web/package.json`
- Modify: `apps/web/src/app/home-page.tsx`
- Modify: `apps/web/src/app/app-shell.test.tsx`
- Modify: `apps/web/e2e/app-shell-home.spec.ts`

- [ ] **Step 1: Write failing Home and production-guard tests**

In `app-shell.test.tsx`, assert that the normal module destination remains and
the development action is additional:

```tsx
expect(screen.getByRole("link", { name: "Paguai" })).toHaveAttribute("href", "/paguai");
expect(screen.getByRole("link", { name: "Abrir protótipo Paguai" })).toHaveAttribute(
  "href",
  "/prototipos/paguai",
);
expect(screen.getByRole("link", { name: "Abrir protótipo Kanban" })).toBeVisible();
expect(screen.queryByRole("link", { name: "Abrir protótipo Atendimento" })).not.toBeInTheDocument();
```

In the existing production-preview `app-shell-home.spec.ts`, sign in, assert
that no `Demonstração` navigation or `Abrir protótipo` action exists, then visit
`/prototipos` directly and assert the wildcard redirects to `/`.

- [ ] **Step 2: Run and observe missing links**

```bash
npm --prefix apps/web test -- --run src/app/app-shell.test.tsx
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts
```

Expected: the unit test FAILS before the Home development actions are added;
the production build stays free of visible prototype routes and controls.

- [ ] **Step 3: Add only available development actions to Home**

Export a lookup from the prototype catalog that returns a path only when its
item is available. In Home, render `Abrir protótipo {label}` only when
`import.meta.env.DEV` and that lookup returns a path. Keep the primary module
card linked to `/paguai` or `/kanban`; never replace the honest product route.

- [ ] **Step 4: Add the isolated development Playwright config**

```ts
import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  testMatch: "module-prototypes.spec.ts",
  use: { baseURL: "http://127.0.0.1:4174" },
  webServer: {
    command: "npm run dev -- --host 127.0.0.1 --port 4174",
    reuseExistingServer: !process.env.CI,
    url: "http://127.0.0.1:4174",
  },
});
```

Add `"test:e2e:prototypes": "playwright test --config playwright.prototypes.config.ts"`
to `package.json`. The default Playwright config remains a production
build/preview gate; this dedicated config is the only browser suite that relies
on `import.meta.env.DEV=true`.

- [ ] **Step 5: Write the Paguai/Kanban browser flow**

Define the helper inside `module-prototypes.spec.ts`:

```ts
async function enterHome(page: Page) {
  await page.goto("/login");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/$/);
}
```

Test desktop and a 390px viewport by calling `page.setViewportSize`; do not use
undefined Playwright projects. Cover the index notice, a local Paguai draft,
conference validation, finance proof guard, keyboard Kanban movement,
read-only projection, theme toggle, focus return, and axe.

- [ ] **Step 6: Run the PR 2 gates and review**

```bash
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
npm --prefix apps/web test -- --run
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts
npm --prefix apps/web run test:e2e:prototypes
git diff --check
```

Write the durable behavior map in Portuguese; do not version screenshots or
temporary evidence. Run one independent read-only review, fix confirmed
findings, rerun affected gates, then commit and open a draft PR from
`codex/ui-paguai-kanban-prototypes` to `codex/ui-workspace-shell`.

### Task 5: Atendimento list/detail prototype

Before this task, create `codex/ui-atendimento-pessoas-prototypes` from the
reviewed PR 2 head. PR 3 remains below 20 changed files.

**Files:**

- Create: all five files under `apps/web/src/features/service-desk/prototype/`
- Modify: `apps/web/src/features/prototypes/prototype-index-page.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`

**Interfaces:**

- `ServiceTicket` includes `id`, `subject`, `company`, `status`, `priority`, `assignee`, `slaMinutes`, `timeline`.
- `addComment(ticketId, body, visibility)` appends one local timeline event.

- [ ] **Step 1: Write failing queue/comment tests**

```tsx
it("filters tickets and appends a labeled local internal comment", async () => {
  const user = userEvent.setup();
  render(<ServiceDeskPrototypePage />);

  await user.click(screen.getByRole("combobox", { name: "Prioridade" }));
  await user.click(screen.getByRole("option", { name: "Alta" }));
  expect(screen.queryByText("Dúvida simples")).not.toBeInTheDocument();

  await user.click(screen.getByRole("button", { name: /Falha no acesso/ }));
  await user.click(screen.getByRole("radio", { name: "Comentário interno" }));
  await user.type(screen.getByLabelText("Comentário"), "Validar com identidade");
  await user.click(screen.getByRole("button", { name: "Adicionar comentário local" }));

  expect(screen.getByText("Validar com identidade")).toBeVisible();
  expect(screen.getByText("Interno · demonstração")).toBeVisible();
});
```

- [ ] **Step 2: Run and observe missing feature**

```bash
npm --prefix apps/web test -- --run src/features/service-desk/prototype/service-desk-prototype-page.test.tsx
```

Expected: FAIL.

- [ ] **Step 3: Implement feature-owned state**

Use a reducer with `selectTicket`, `setPriorityFilter`, `assignToMe`, and
`addComment`. Generate deterministic local event ids from the current event
count; do not use random ids or dates in tests.

```ts
const event: TicketEvent = {
  id: `demo-event-${ticket.timeline.length + 1}`,
  kind: visibility === "internal" ? "internal-comment" : "public-comment",
  body,
  label: visibility === "internal" ? "Interno · demonstração" : "Público · demonstração",
};
```

- [ ] **Step 4: Compose responsive queue and detail**

Desktop uses CSS grid `minmax(280px, 0.8fr) minmax(0, 1.2fr)`. Mobile opens the
same detail body in `DetailDrawer`. Include explicit `Dados insuficientes` for
metrics without fixture evidence and `DemoAttachmentField` in the comment area.

After the page exists, append `/prototipos/atendimento` as a non-navigation
route, add it to `AppPath`, and set only Atendimento's catalog item to
`available: true`.

- [ ] **Step 5: Run tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/service-desk/prototype/service-desk-prototype-page.test.tsx
git add apps/web/src/features/service-desk/prototype apps/web/src/features/prototypes/prototype-index-page.tsx apps/web/src/shared/routing/app-paths.ts apps/web/src/app/app-route-manifest.tsx
git commit -m "feat(web): add Atendimento queue prototype"
```

Expected: PASS.

### Task 6: Pessoas privacy-aware prototype

**Files:**

- Create: all five files under `apps/web/src/features/people/prototype/`
- Modify: `apps/web/src/features/prototypes/prototype-index-page.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`

**Interfaces:**

- `PeopleSection = "directory" | "absences" | "hr-review" | "team"`.
- `EmployeeSummary` deliberately excludes diagnosis, CID, document URL, and medical notes.
- `createAbsenceDraft(period, kind)` appends a local self-service draft.

- [ ] **Step 1: Write failing privacy and absence tests**

```tsx
it("creates a local absence draft without exposing medical details to the manager view", async () => {
  const user = userEvent.setup();
  render(<PeoplePrototypePage />);

  await user.click(screen.getByRole("tab", { name: "Ausências" }));
  await user.click(screen.getByRole("button", { name: "Novo rascunho de ausência" }));
  await user.type(screen.getByLabelText("Início"), "2026-08-20");
  await user.type(screen.getByLabelText("Fim"), "2026-08-21");
  await user.click(screen.getByRole("button", { name: "Salvar rascunho local" }));
  expect(screen.getByText("Rascunho local")).toBeVisible();

  await user.click(screen.getByRole("tab", { name: "Equipe" }));
  expect(screen.queryByText(/CID|diagnóstico|nota médica/i)).not.toBeInTheDocument();
});
```

- [ ] **Step 2: Run and observe missing feature**

```bash
npm --prefix apps/web test -- --run src/features/people/prototype/people-prototype-page.test.tsx
```

Expected: FAIL.

- [ ] **Step 3: Implement privacy-minimized types and local state**

```ts
export type EmployeeSummary = {
  id: string;
  name: string;
  company: "alfa" | "beta";
  team: string;
  role: string;
  availability: "available" | "absent" | "planned-absence";
};
```

The restricted RH fixture stores only `documentState: "restricted" | "temporary-access"`;
it never stores medical content.

- [ ] **Step 4: Compose four tabs and contextual attachments**

Directory supports text search and an operational profile drawer. Absences uses
a validated local dialog. RH Review shows `Acesso temporário demonstrativo`
after explicit confirmation and embeds `DemoAttachmentField`. Team renders only
name, period, status, and coverage need.

After the page exists, append `/prototipos/pessoas` as a non-navigation route,
add it to `AppPath`, and set only Pessoas's catalog item to `available: true`.

- [ ] **Step 5: Run tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/people/prototype/people-prototype-page.test.tsx
git add apps/web/src/features/people/prototype apps/web/src/features/prototypes/prototype-index-page.tsx apps/web/src/shared/routing/app-paths.ts apps/web/src/app/app-route-manifest.tsx
git commit -m "feat(web): add privacy-aware Pessoas prototype"
```

Expected: PASS.

### Task 7: PR 3 browser coverage and documentation

**Files:**

- Modify: `apps/web/e2e/module-prototypes.spec.ts`
- Modify: `apps/web/src/app/home-page.tsx`
- Modify: `apps/web/src/app/app-shell.test.tsx`
- Create: `docs/verification/frontend-atendimento-pessoas-prototypes-test-cases.md`

**Interfaces:**

- Produces: browser proof for every local action and a concise evidence map.

- [ ] **Step 1: Write failing Home availability tests**

Extend the development Home assertions from Task 4:

```tsx
expect(screen.getByRole("link", { name: "Abrir protótipo Atendimento" })).toHaveAttribute(
  "href",
  "/prototipos/atendimento",
);
expect(screen.getByRole("link", { name: "Abrir protótipo Pessoas" })).toHaveAttribute(
  "href",
  "/prototipos/pessoas",
);
```

Run `app-shell.test.tsx`, observe failure, then let the same catalog lookup
render those actions now that both catalog items are available.

- [ ] **Step 2: Extend the development Playwright flow**

```ts
test("planned module prototypes remain clearly local and navigable", async ({ page }) => {
  await enterHome(page);
  await page.getByRole("link", { name: "Demonstração" }).click();
  await expect(page.getByText("Dados de demonstração")).toBeVisible();

  await page.goto("/prototipos/atendimento");
  await expect(page.getByRole("heading", { name: "Atendimento" })).toBeVisible();
  await page.getByRole("button", { name: /Falha no acesso/ }).click();
  await page.getByRole("radio", { name: "Comentário interno" }).click();
  await page.getByLabel("Comentário").fill("Validar com identidade");
  await page.getByRole("button", { name: "Adicionar comentário local" }).click();
  await expect(page.getByText("Interno · demonstração")).toBeVisible();

  await page.goto("/prototipos/pessoas");
  await page.getByRole("tab", { name: "Equipe" }).click();
  await expect(page.getByText(/CID|diagnóstico|nota médica/i)).toHaveCount(0);
});
```

- [ ] **Step 3: Run desktop and mobile browser tests**

```bash
npm --prefix apps/web run test:e2e:prototypes
```

The spec contains separate desktop and 390px tests using
`page.setViewportSize`. Expected: PASS after fixing only concrete responsive,
overflow, and focus failures.

- [ ] **Step 4: Write the verification map**

Document each action as `user path -> English BDD -> Vitest/Playwright path -> status`.
Explicitly state that no API, authorization, storage upload, or persistence is
under test.

- [ ] **Step 5: Run the complete PR gate**

```bash
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
npm --prefix apps/web test -- --run
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts
npm --prefix apps/web run test:e2e:prototypes
git diff --check
```

Expected: PASS.

- [ ] **Step 6: Commit and independent review**

```bash
git add apps/web/e2e/module-prototypes.spec.ts apps/web/src/app/home-page.tsx apps/web/src/app/app-shell.test.tsx docs/verification/frontend-atendimento-pessoas-prototypes-test-cases.md
git commit -m "test(web): verify Atendimento and Pessoas prototypes"
```

Run one independent read-only review, fix confirmed findings, rerun affected
tests, then push `codex/ui-atendimento-pessoas-prototypes` and open a draft PR
targeting `codex/ui-paguai-kanban-prototypes`.
