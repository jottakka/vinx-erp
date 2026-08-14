# Agent and Notification Prototypes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a truthful, development-only agent prototype that can be used in a right-side dock or a dedicated page without losing its local conversation, plus a functional notification drawer.

**Architecture:** Feature-owned providers hold local prototype state inside the protected workspace boundary. `AppHeader` emits presentation intents; `AgentDock`, `AgentPage`, and `NotificationDrawer` consume those providers. The shared agent conversation renders typed outcomes—answer, clarification, denial, and confirmation proposal—while never running a mutation or pretending to contact a backend.

**Tech Stack:** React 19, TypeScript 6, React Router 7, MUI 9.3.1, Vitest, Testing Library, Playwright.

## Global Constraints

- Create `codex/ui-agent-prototypes` from the reviewed `codex/ui-atendimento-pessoas-prototypes` head.
- The `/agente` route and prototype controls render only when `import.meta.env.DEV` is true.
- Every fixture surface visibly says `Dados de demonstração`; all changes reset on reload.
- Keep one conversation state while moving between the dock and `/agente`.
- The confirmation card proposes an action but exposes only `Voltar` and `Entendi`; it never mutates domain state.
- Denied and clarification outcomes stay visibly distinct from errors.
- Do not add a command palette, business chat, runtime agent, provider, tools, streaming protocol, persistence, telemetry, or backend contract.
- Do not add `react-resizable-panels` or any other dependency. MUI Drawer is sufficient for the current fixed-width dock.
- Never access, list, or execute `quality/private`.

---

## File Structure

**Create agent files**

- `apps/web/src/features/agent/prototype/agent-prototype-types.ts`
- `apps/web/src/features/agent/prototype/agent-prototype-fixtures.ts`
- `apps/web/src/features/agent/prototype/agent-prototype-context.tsx`
- `apps/web/src/features/agent/prototype/agent-conversation.tsx`
- `apps/web/src/features/agent/prototype/agent-confirmation-card.tsx`
- `apps/web/src/features/agent/prototype/agent-dock.tsx`
- `apps/web/src/features/agent/prototype/agent-page.tsx`
- `apps/web/src/features/agent/prototype/agent-prototype.test.tsx`

**Create notification files**

- `apps/web/src/features/notifications/prototype/notification-prototype-types.ts`
- `apps/web/src/features/notifications/prototype/notification-prototype-fixtures.ts`
- `apps/web/src/features/notifications/prototype/notification-prototype-context.tsx`
- `apps/web/src/features/notifications/prototype/notification-drawer.tsx`
- `apps/web/src/features/notifications/prototype/notification-drawer.test.tsx`

**Modify**

- `apps/web/src/app/app-shell.tsx`
- `apps/web/src/app/shell/app-header.tsx`
- `apps/web/src/shared/routing/app-paths.ts`
- `apps/web/src/app/app-route-manifest.tsx`
- `apps/web/src/app/router.test.tsx`
- `apps/web/playwright.prototypes.config.ts`
- `apps/web/e2e/app-shell-home.spec.ts`
- Create: `apps/web/e2e/agent-notifications.spec.ts`
- Create: `docs/verification/frontend-agent-notifications-test-cases.md`

### Task 1: Define typed agent outcomes and local state

**Files:**

- Create: `agent-prototype-types.ts`
- Create: `agent-prototype-fixtures.ts`
- Create: `agent-prototype-context.tsx`
- Create: `agent-prototype.test.tsx`

**Interfaces:**

```ts
export type AgentOutcome =
  | { kind: "grounded-answer"; text: string; sources: AgentSource[] }
  | { kind: "clarification"; text: string; suggestions: string[] }
  | { kind: "denied"; text: string; reason: string }
  | { kind: "requires-confirmation"; text: string; proposal: AgentProposal };

export type AgentMessage = {
  id: string;
  author: "user" | "agent";
  createdAt: string;
  outcome?: AgentOutcome;
  text: string;
};

export type AgentPrototypeState = {
  dockOpen: boolean;
  lastContextPath: string;
  messages: AgentMessage[];
  pending: boolean;
};
```

`AgentProposal` contains only display-safe fields: `title`, `company`,
`summary`, and `impact`. `AgentSource` contains `label` and an internal `path`.

- [ ] **Step 1: Write failing provider tests**

```tsx
it("keeps one conversation while dock visibility changes", async () => {
  const user = userEvent.setup();
  render(<AgentHarness />);

  await user.type(screen.getByRole("textbox", { name: "Mensagem para o agente" }), "Quais pagamentos vencem hoje?");
  await user.click(screen.getByRole("button", { name: "Enviar" }));
  expect(await screen.findByText(/encontrei três solicitações/i)).toBeVisible();

  await user.click(screen.getByRole("button", { name: "Fechar agente" }));
  await user.click(screen.getByRole("button", { name: "Abrir agente" }));
  expect(screen.getByText("Quais pagamentos vencem hoje?")).toBeVisible();
});

it.each([
  ["pagamentos", "grounded-answer"],
  ["faça isso", "clarification"],
  ["mostre dados médicos", "denied"],
  ["aprove a solicitação", "requires-confirmation"],
])("maps %s to a deterministic %s outcome", async (prompt, expectedKind) => {
  const result = resolvePrototypeOutcome(prompt);
  expect(result.kind).toBe(expectedKind);
});
```

- [ ] **Step 2: Run and observe missing provider**

```bash
npm --prefix apps/web test -- --run src/features/agent/prototype/agent-prototype.test.tsx
```

Expected: FAIL because the agent prototype files do not exist.

- [ ] **Step 3: Implement deterministic fixtures and reducer**

Use normalized lowercase keyword matching only inside the fixture resolver. It
must have a total fallback to `clarification`; it must not inspect route state,
call timers longer than one animation frame, or create nondeterministic IDs.

```ts
export function resolvePrototypeOutcome(prompt: string): AgentOutcome {
  const normalized = prompt.trim().toLocaleLowerCase("pt-BR");
  if (normalized.includes("médic") || normalized.includes("cid")) return deniedFixture;
  if (normalized.includes("aprov") || normalized.includes("pagar")) return confirmationFixture;
  if (normalized.includes("pagamento") || normalized.includes("vence")) return groundedFixture;
  return clarificationFixture;
}
```

The reducer owns `openDock`, `closeDock`, `setContext`, `send`, and
`completeResponse`. `send` ignores blank input and rejects more than 1,000
characters with a visible validation message. Tests use a deterministic
microtask completion; there is no fake streaming.

- [ ] **Step 4: Implement provider contract**

```ts
type AgentPrototypeContextValue = AgentPrototypeState & {
  error: string | null;
  openDock(contextPath: string): void;
  closeDock(): void;
  sendMessage(text: string): Promise<void>;
};
```

Throw a descriptive developer error when the hook is used outside the provider.
Do not expose the reducer or fixture matcher outside the feature.

- [ ] **Step 5: Run focused tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/agent/prototype/agent-prototype.test.tsx
git add apps/web/src/features/agent/prototype
git commit -m "feat(web): add typed agent prototype state"
```

Expected: provider tests PASS.

### Task 2: Build the shared conversation and confirmation proposal

**Files:**

- Create: `agent-conversation.tsx`
- Create: `agent-confirmation-card.tsx`
- Modify: `agent-prototype.test.tsx`

**Interfaces:**

- `AgentConversation` props: `{ contextLabel: string; compact?: boolean }`.
- `AgentConfirmationCard` props: `{ proposal: AgentProposal }`.
- Both read state/actions through `useAgentPrototype`; presentation never owns conversation state.

- [ ] **Step 1: Write failing semantic and safety tests**

```tsx
it("renders sources as internal links and labels a proposed confirmation", async () => {
  render(<AgentHarness initialPrompt="Quais pagamentos vencem hoje?" />);
  expect(await screen.findByRole("link", { name: "Fila financeira do Paguai" })).toHaveAttribute(
    "href",
    "/prototipos/paguai",
  );
  expect(screen.getByText("Resposta de demonstração")).toBeVisible();
});

it("never exposes an execute action on a confirmation proposal", async () => {
  render(<AgentHarness initialPrompt="Aprove a solicitação" />);
  expect(await screen.findByRole("heading", { name: "Confirmação necessária" })).toBeVisible();
  expect(screen.queryByRole("button", { name: /aprovar|executar|confirmar pagamento/i })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Entendi" })).toBeVisible();
});
```

- [ ] **Step 2: Run and observe missing presentation**

```bash
npm --prefix apps/web test -- --run src/features/agent/prototype/agent-prototype.test.tsx
```

Expected: FAIL on missing conversation components.

- [ ] **Step 3: Implement conversation semantics**

Render messages in a labelled log (`role="log"`, `aria-live="polite"`) and use
headings inside outcome cards. Grounded answers render source links;
clarification renders suggestion buttons that only populate the composer;
denied renders a warning with the safe reason; confirmation renders the
non-mutating proposal card.

The composer uses a labelled multiline field, a character count, `Enviar`, and
`Limpar texto`. Enter submits only without Shift. Disable sending while pending
and expose `aria-busy` on the conversation region.

- [ ] **Step 4: Implement the non-mutating card**

```tsx
<Card variant="outlined">
  <CardHeader title="Confirmação necessária" subheader="Proposta de demonstração — nenhuma ação foi executada" />
  <CardContent>{/* company, summary, impact */}</CardContent>
  <CardActions>
    <Button onClick={onDismiss}>Voltar</Button>
    <Button onClick={onAcknowledge} variant="contained">Entendi</Button>
  </CardActions>
</Card>
```

Both buttons only set local presentation state; neither changes a Paguai
fixture nor claims authorization.

- [ ] **Step 5: Run focused tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/agent/prototype/agent-prototype.test.tsx
git add apps/web/src/features/agent/prototype
git commit -m "feat(web): render safe agent prototype outcomes"
```

Expected: semantic and safety tests PASS.

### Task 3: Mount the right dock and dedicated page

**Files:**

- Create: `agent-dock.tsx`
- Create: `agent-page.tsx`
- Modify: `apps/web/src/app/app-shell.tsx`
- Modify: `apps/web/src/app/shell/app-header.tsx`
- Modify: `apps/web/src/shared/routing/app-paths.ts`
- Modify: `apps/web/src/app/app-route-manifest.tsx`
- Modify: `apps/web/src/app/router.test.tsx`
- Modify: `agent-prototype.test.tsx`

**Interfaces:**

- `AppHeader` receives `onOpenAgent` only when prototypes are enabled.
- `AgentDock` receives `{ desktop: boolean }` and derives open/state from its provider.
- `AgentPage` uses the same `AgentConversation` with `compact={false}`.

- [ ] **Step 1: Write failing dock/route tests**

```tsx
it("opens the agent from the header and exposes its dedicated route", async () => {
  const user = userEvent.setup();
  const router = renderRoute("/", await signedInGateway());

  await user.click(screen.getByRole("button", { name: "Abrir agente" }));
  expect(screen.getByRole("complementary", { name: "Agente VINX" })).toBeVisible();
  await user.click(screen.getByRole("link", { name: "Abrir em tela inteira" }));
  expect(await screen.findByRole("heading", { name: "Agente VINX" })).toBeVisible();
  expect(router.state.location.pathname).toBe("/agente");
});

it("keeps the dock conversation after visiting the dedicated page", async () => {
  // Send in the dock, navigate to /agente, return to /, and assert the same message remains.
});
```

- [ ] **Step 2: Run and observe missing integration**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/features/agent/prototype/agent-prototype.test.tsx
```

Expected: FAIL because header trigger, dock, and `/agente` are absent.

- [ ] **Step 3: Mount one provider at the workspace boundary**

Inside the protected shell, wrap header, outlet, and dock with
`AgentPrototypeProvider`. Do not mount a second provider inside `AgentPage`.
The provider exists only for development prototype routes/controls.

```tsx
<AgentPrototypeProvider>
  <AppHeader onOpenAgent={() => openDock(location.pathname)} />
  <Outlet />
  <AgentDock desktop={desktop} />
</AgentPrototypeProvider>
```

If hook placement prevents this exact shape, add a private `WorkspaceContent`
child under the provider; do not lift agent state into the generic shell API.

- [ ] **Step 4: Implement responsive drawer behavior**

Use `variant={desktop ? "persistent" : "temporary"}`, `anchor="right"`, width
`420px` on desktop and `min(100vw, 420px)` on mobile. The dock is an `aside`
named `Agente VINX`. It has explicit close and `Abrir em tela inteira` actions.
On desktop, main content gains right margin only while the persistent dock is
open; this is a shell layout concern, not feature state.

The full page includes `Voltar para {contextLabel}`. Activating it navigates to
`lastContextPath` and calls `openDock(lastContextPath)` after navigation, so the
same conversation is visible in the dock.

- [ ] **Step 5: Register `/agente` as development-only**

Add `/agente` to `AppPath` and its route metadata to the development section of
the single route manifest. It does not appear in primary navigation; only
header/dock links reach it. Test the existing pure route selector with `false`
and assert that neither `/agente` nor its trigger is returned; do not mutate a
module-level `import.meta.env` value in an already imported manifest.

- [ ] **Step 6: Run focused tests and commit**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/features/agent/prototype/agent-prototype.test.tsx
git add apps/web/src/app apps/web/src/shared/routing/app-paths.ts apps/web/src/features/agent/prototype
git commit -m "feat(web): add docked and full-page agent prototype"
```

Expected: route, continuity, responsiveness contract, and production exclusion tests PASS.

### Task 4: Add local notification inbox drawer

**Files:**

- Create: all five files under `apps/web/src/features/notifications/prototype/`
- Modify: `apps/web/src/app/app-shell.tsx`
- Modify: `apps/web/src/app/shell/app-header.tsx`

**Interfaces:**

```ts
export type PrototypeNotification = {
  id: string;
  title: string;
  description: string;
  createdAtLabel: string;
  path?: string;
  read: boolean;
  tone: "info" | "attention" | "success";
};
```

The provider returns `{ notifications, unreadCount, open, openDrawer,
closeDrawer, markRead, markAllRead }`.

- [ ] **Step 1: Write failing notification tests**

```tsx
it("opens a labelled inbox and marks one item read locally", async () => {
  const user = userEvent.setup();
  render(<NotificationHarness />);

  expect(screen.getByRole("button", { name: "Notificações, 2 não lidas" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Notificações, 2 não lidas" }));
  expect(screen.getByRole("heading", { name: "Notificações" })).toBeVisible();
  expect(screen.getByText("Dados de demonstração")).toBeVisible();

  await user.click(screen.getByRole("button", { name: "Marcar Solicitação devolvida como lida" }));
  expect(screen.getByRole("button", { name: "Notificações, 1 não lida" })).toBeVisible();
});
```

- [ ] **Step 2: Run and observe missing feature**

```bash
npm --prefix apps/web test -- --run src/features/notifications/prototype/notification-drawer.test.tsx
```

Expected: FAIL because the notification feature does not exist.

- [ ] **Step 3: Implement provider and drawer**

Use a reducer with idempotent `markRead` and `markAllRead`. The right drawer is
temporary at all breakpoints so it cannot conflict with the persistent agent
dock. Opening notifications closes only its own trigger menu; it does not erase
or recreate agent state.

Each row exposes title, description, textual read status, timestamp label, and
an optional internal link. Tone always includes an icon and text, never color
alone. `Marcar todas como lidas` is disabled at zero unread.

- [ ] **Step 4: Mount under the same workspace lifecycle**

Wrap `AppHeader` and `NotificationDrawer` in one
`NotificationPrototypeProvider`, sibling to the agent provider. Pass explicit
header callbacks/count instead of importing the context into the generic app
header. Hide the trigger outside development.

- [ ] **Step 5: Run focused tests and commit**

```bash
npm --prefix apps/web test -- --run src/features/notifications/prototype/notification-drawer.test.tsx src/app/router.test.tsx
git add apps/web/src/app apps/web/src/features/notifications/prototype
git commit -m "feat(web): add notification inbox prototype"
```

Expected: unread state and shell integration tests PASS.

### Task 5: Verify integrated behavior, accessibility, and documentation

**Files:**

- Create: `apps/web/e2e/agent-notifications.spec.ts`
- Create: `docs/verification/frontend-agent-notifications-test-cases.md`
- Modify: `apps/web/playwright.prototypes.config.ts`
- Modify: `apps/web/e2e/app-shell-home.spec.ts`

- [ ] **Step 1: Extend both development and production browser gates**

Change the dedicated development config to:

```ts
testMatch: /(?:module-prototypes|agent-notifications)\.spec\.ts/,
```

In the default production-preview `app-shell-home.spec.ts`, assert that
`Abrir agente` and `Notificações` controls are absent, direct navigation to
`/agente` returns to `/`, and the previously added `/prototipos` guard still
passes. This is the production-build proof; unit tests cover the pure disabled
route selector.

- [ ] **Step 2: Write end-to-end tests before final polish**

Cover:

1. open dock from `/`, send a grounded query, and inspect its source;
2. expand to `/agente`, return to `/`, and see the same conversation;
3. render clarification, denial, and confirmation proposal without execution;
4. open notifications, mark one and all read, and verify visible local-state notice;
5. repeat critical dock flow at 390px viewport using temporary drawer;
6. toggle light/dark while dock is open and verify state remains;
7. run axe on `/` with the dock, `/agente`, and notification drawer open.

```bash
npm --prefix apps/web run test:e2e:prototypes -- --grep "agente|notificações"
```

Expected initially: FAIL on missing selectors or accessibility defects.

- [ ] **Step 3: Fix only evidenced integration defects**

Correct focus order, accessible names, overflow, responsive width, and contrast
found by tests. Do not add speculative abstractions or new dependencies.

- [ ] **Step 4: Document durable manual cases in Portuguese**

Record route/viewport, action, expected visible outcome, keyboard path, theme,
and the explicit guarantee that no external action occurs. Do not store
screenshots or temporary evidence in the repository.

- [ ] **Step 5: Run the complete public frontend verification**

```bash
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
npm --prefix apps/web test -- --run
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts
npm --prefix apps/web run test:e2e:prototypes
./scripts/verify-public.sh
git diff --check
```

Expected: all commands PASS. Run `./scripts/verify-public.sh` only once for the
behavior-changing branch immediately before push.

- [ ] **Step 6: Independent review with exact requested model**

```bash
opencode run --model alibaba-token-plan/qwen3.8-max \
  "Review the current agent/notification prototype branch against AGENTS.md, the approved design, and the implementation plans. Never access quality/private. Report only concrete correctness, React architecture, accessibility, prototype-honesty, and test gaps with file and line evidence; end with PASS or CHANGES REQUIRED."
```

Address valid findings, rerun the affected focused tests, and record the review
summary in the pull request description rather than a versioned report.

- [ ] **Step 7: Commit the final proof**

```bash
git add apps/web/playwright.prototypes.config.ts apps/web/e2e/app-shell-home.spec.ts apps/web/e2e/agent-notifications.spec.ts docs/verification/frontend-agent-notifications-test-cases.md
git commit -m "test(web): verify agent and notification prototypes"
```

Expected: clean branch ready for one stacked pull request targeting
`codex/ui-atendimento-pessoas-prototypes`.

## Definition of Done

- The agent opens in a right dock and `/agente` with one preserved local conversation.
- Grounded, clarification, denied, and confirmation-required outcomes are visibly distinct and tested.
- The confirmation proposal cannot execute or claim a domain mutation.
- Notification unread state is functional, local, labelled, and tested.
- Prototype routes and controls are absent from production builds.
- Light/dark, desktop/mobile, keyboard, focus, and axe checks pass.
- No dependency was added.
- Public verification and independent Qwen review pass.
