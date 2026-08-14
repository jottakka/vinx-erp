# Neutral Workspace Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a neutral light/dark theme, one typed route/navigation manifest, honest module preparation destinations, and a smaller composable authenticated shell.

**Architecture:** Keep `ProtectedRoute -> AppShell -> Outlet`. Move the pure safe-return-path allowlist to `shared/routing`, compose route metadata in the app layer, and split header/navigation rendering from shell state. Product routes remain honest preparation pages; no prototype data or agent UI enters this PR.

**Tech Stack:** React 19, TypeScript 6, React Router 7, MUI 9.3.1, Vitest, Testing Library, Playwright, Biome.

## Global Constraints

- Branch from the current #194 head into `codex/ui-workspace-shell`; never write directly to `main`.
- Offer only `light` and `dark`; normalize missing, `system`, or invalid stored mode to `light`.
- Use off-white/graphite in light mode and charcoal/ivory in dark mode; copper is the restrained interaction accent.
- Company identity may color a badge but never recolors the shell or represents authorization.
- Do not add a dependency.
- Keep one typed definition for route, navigation label, icon, group, and safe return-path behavior.
- Do not expose fake domain metrics, notifications, agent UI, PWA, offline cache, queue, or replay.
- Preserve skip link, landmarks, mobile focus restoration, safe logout failure, and all-authorized Home.
- Never access, list, or execute `quality/private`.

---

## File Structure

**Create**

- `apps/web/src/shared/routing/app-paths.ts` — pure path literals and safe return-path validation.
- `apps/web/src/app/app-route-manifest.tsx` — app-layer route and navigation composition.
- `apps/web/src/app/shell/app-header.tsx` — toolbar rendering and session actions.
- `apps/web/src/app/shell/navigation-list.tsx` — grouped desktop/mobile navigation renderer.
- `apps/web/src/app/feature-preparation-page.tsx` — honest reusable destination for #193.
- `apps/web/src/shared/ui/page-header.tsx` — semantic page title/description/action pattern.
- `apps/web/src/app/feature-preparation-page.test.tsx` — route-content and return-path tests.

**Modify**

- `apps/web/src/app/theme/palette-primitives.ts`
- `apps/web/src/app/theme/create-vinx-theme.ts`
- `apps/web/src/app/theme/vinx-theme-provider.tsx`
- `apps/web/src/app/theme/create-vinx-theme.test.ts`
- `apps/web/src/app/theme/visual-preferences.test.tsx`
- `apps/web/src/shared/ui/appearance-menu.tsx`
- `apps/web/src/shared/ui/appearance-menu.test.tsx`
- `apps/web/src/app/app-shell.tsx`
- `apps/web/src/app/app-shell.test.tsx`
- `apps/web/src/app/home-page.tsx`
- `apps/web/src/app/help-page.tsx`
- `apps/web/src/app/router.tsx`
- `apps/web/src/app/router.test.tsx`
- `apps/web/src/features/auth/login-page.tsx`
- `apps/web/src/features/auth/login-page.test.tsx`
- `apps/web/src/app/protected-route.tsx`
- `apps/web/e2e/app-shell-home.spec.ts`
- `apps/web/e2e/visual-theme-login.spec.ts`
- `docs/manuals/frontend/app-shell-home.md`
- `docs/manuals/frontend/visual-preferences.md`

**Delete after callers migrate**

- `apps/web/src/app/app-navigation.tsx`

### Task 1: Neutral two-mode theme

**Files:**

- Modify: `apps/web/src/app/theme/palette-primitives.ts`
- Modify: `apps/web/src/app/theme/create-vinx-theme.ts`
- Modify: `apps/web/src/app/theme/vinx-theme-provider.tsx`
- Modify: `apps/web/src/app/theme/create-vinx-theme.test.ts`
- Modify: `apps/web/src/app/theme/visual-preferences.test.tsx`
- Modify: `apps/web/src/shared/ui/appearance-menu.tsx`
- Modify: `apps/web/src/shared/ui/appearance-menu.test.tsx`
- Modify: `apps/web/src/features/auth/login-page.test.tsx`

**Interfaces:**

- Consumes: MUI `ThemeProvider`, `useColorScheme`, existing density context.
- Produces: `ColorMode = "light" | "dark"`, neutral semantic palettes, `defaultMode="light"`.

- [ ] **Step 1: Write failing theme-mode tests**

Add assertions that the menu has exactly the two requested modes and no system item:

```tsx
await user.click(
  screen.getByRole("button", { name: "Abrir preferências visuais" }),
);
expect(screen.getByRole("menuitem", { name: "Tema: claro" })).toBeVisible();
expect(screen.getByRole("menuitem", { name: "Tema: escuro" })).toBeVisible();
expect(
  screen.queryByRole("menuitem", { name: "Tema: sistema" }),
).not.toBeInTheDocument();
```

Change storage fallback expectations to `light` and assert an old `system` value is rejected:

```ts
const invalidModeStorage = manager({
  key: "mui-mode",
  storageWindow: invalidModeWindow,
});
expect(invalidModeStorage.get("light")).toBe("light");

const legacySystemWindow = {
  ...invalidModeWindow,
  localStorage: { getItem: () => "system", setItem: () => {} },
} as unknown as Window;
expect(
  manager({ key: "mui-mode", storageWindow: legacySystemWindow }).get("light"),
).toBe("light");
```

- [ ] **Step 2: Run the focused tests and observe failure**

Run:

```bash
npm --prefix apps/web test -- --run src/shared/ui/appearance-menu.test.tsx src/app/theme/create-vinx-theme.test.ts
```

Expected: FAIL because `Tema: sistema` still exists and `system` is accepted.

- [ ] **Step 3: Implement the two-mode contract**

Use this mode type and remove the system menu item:

```ts
type ColorMode = "light" | "dark";

function selectMode(nextMode: ColorMode) {
  setMode(nextMode);
  closeMenu();
}
```

Set the provider default:

```tsx
<ThemeProvider
  colorSchemeStorageKey="scheme"
  defaultMode="light"
  disableTransitionOnChange
  modeStorageKey="mode"
  noSsr
  storageManager={vinxStorageManager}
  theme={theme}
>
```

Limit accepted stored modes:

```ts
const supportedModes = new Set(["light", "dark"]);
```

Use these shell primitives, preserving contrast-tested company slots:

```ts
export const lightPalette = {
  primary: {
    light: "#c98761",
    main: "#a85f39",
    dark: "#7f4326",
    contrastText: "#fffaf2",
  },
  navigation: {
    light: "#3f3c37",
    main: "#24221f",
    dark: "#161513",
    contrastText: "#f8f5ee",
  },
  background: { default: "#f4f2ed", paper: "#fbfaf7" },
  text: {
    primary: "#24221f",
    secondary: "#6f6a62",
    disabled: "#99938a",
  },
  divider: "#dedbd4",
} as const;

export const darkPalette = {
  primary: {
    light: "#e0a47d",
    main: "#c47a4f",
    dark: "#965735",
    contrastText: "#17120f",
  },
  navigation: {
    light: "#3a3935",
    main: "#242421",
    dark: "#151514",
    contrastText: "#f3f0e8",
  },
  background: { default: "#111110", paper: "#191918" },
  text: {
    primary: "#f3f0e8",
    secondary: "#aaa69d",
    disabled: "#77736c",
  },
  divider: "#32312e",
} as const;
```

Retain semantic success/warning/error from MUI and update company slots with
muted, contrast-valid values rather than shell colors.

- [ ] **Step 4: Run tests and contrast assertions**

Run:

```bash
npm --prefix apps/web test -- --run src/shared/ui/appearance-menu.test.tsx src/app/theme/create-vinx-theme.test.ts src/app/theme/color-token-guard.test.ts
```

Expected: PASS.

- [ ] **Step 5: Commit the theme change**

```bash
git add apps/web/src/app/theme apps/web/src/shared/ui/appearance-menu.tsx apps/web/src/shared/ui/appearance-menu.test.tsx
git commit -m "feat(web): refine neutral light and dark themes"
```

### Task 2: Pure safe paths and one route manifest

**Files:**

- Create: `apps/web/src/shared/routing/app-paths.ts`
- Create: `apps/web/src/app/app-route-manifest.tsx`
- Create: `apps/web/src/shared/ui/page-header.tsx`
- Create: `apps/web/src/app/feature-preparation-page.tsx`
- Create: `apps/web/src/app/feature-preparation-page.test.tsx`
- Modify: `apps/web/src/features/auth/login-page.tsx`
- Modify: `apps/web/src/app/protected-route.tsx`
- Modify: `apps/web/src/app/router.tsx`
- Modify: `apps/web/src/app/router.test.tsx`
- Delete: `apps/web/src/app/app-navigation.tsx`

**Interfaces:**

- Produces: `APP_PATHS`, `AppPath`, `isKnownAppPath`, `resolveAppPath`, `PageHeader`, `FeaturePreparationPage`, `appRouteManifest`, `navigationGroups`.
- Consumers: login return-path resolution, protected-route capture, router children, shell navigation.

- [ ] **Step 1: Write failing route and preparation-page tests**

Add this table-driven test:

```tsx
it.each([
  ["/paguai", "Paguai"],
  ["/atendimento", "Atendimento"],
  ["/kanban", "Kanban"],
  ["/pessoas", "Pessoas"],
])("protects and renders %s", async (path, heading) => {
  const router = renderRoute(path, await signedInGateway());
  expect(await screen.findByRole("heading", { name: heading })).toBeVisible();
  expect(router.state.location.pathname).toBe(path);
});
```

Add a unit case in `router.test.tsx` that an anonymous `/paguai` returns there
after login, while `/paguai?empresa=alfa` still falls back to `/`.

Create `feature-preparation-page.test.tsx` with the honest-state contract:

```tsx
it("labels an unfinished module honestly and returns to Home", async () => {
  const user = userEvent.setup();
  const router = renderPreparationPage("Paguai");

  expect(screen.getByRole("heading", { name: "Paguai" })).toBeVisible();
  expect(screen.getByText("Módulo em preparação")).toBeVisible();
  expect(screen.queryByRole("button", { name: /criar|aprovar|pagar/i })).not.toBeInTheDocument();

  await user.click(screen.getByRole("link", { name: "Voltar ao Início" }));
  expect(router.state.location.pathname).toBe("/");
});
```

- [ ] **Step 2: Run and observe missing routes**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/app/feature-preparation-page.test.tsx
```

Expected: FAIL because module paths and the preparation component do not exist.

- [ ] **Step 3: Create the pure path module**

```ts
export const APP_PATHS = [
  "/",
  "/ajuda",
  "/paguai",
  "/atendimento",
  "/kanban",
  "/pessoas",
] as const;

export type AppPath = (typeof APP_PATHS)[number];

export function isKnownAppPath(value: unknown): value is AppPath {
  return typeof value === "string" && APP_PATHS.includes(value as AppPath);
}

export function resolveAppPath(value: unknown): AppPath {
  return isKnownAppPath(value) ? value : "/";
}
```

Import this pure module from auth and `ProtectedRoute`; neither feature imports
an app rendering module.

- [ ] **Step 4: Create the page pattern, app-layer manifest, and router children**

Create the semantic header and honest page before importing it from the
manifest:

```tsx
export function PageHeader({ title, description, eyebrow, actions }: PageHeaderProps) {
  return (
    <Stack component="header" spacing={1}>
      {eyebrow ? <Typography color="primary" variant="overline">{eyebrow}</Typography> : null}
      <Stack alignItems={{ sm: "center" }} direction={{ xs: "column", sm: "row" }} gap={2}>
        <Box sx={{ flex: 1 }}>
          <Typography component="h1" variant="h4">{title}</Typography>
          <Typography color="text.secondary">{description}</Typography>
        </Box>
        {actions}
      </Stack>
    </Stack>
  );
}
```

```tsx
export function FeaturePreparationPage({ feature, scope }: FeaturePreparationPageProps) {
  return (
    <Stack spacing={3}>
      <PageHeader title={feature} description={scope} eyebrow="Módulo em preparação" />
      <Card variant="outlined">
        <CardContent>
          <Typography color="text.secondary">
            Esta área ainda não possui dados ou ações operacionais.
          </Typography>
        </CardContent>
      </Card>
      <Button component={RouterLink} to="/" variant="outlined">Voltar ao Início</Button>
    </Stack>
  );
}
```

Define exact metadata:

```tsx
export type AppRouteDefinition = {
  path: AppPath;
  label: string;
  group: "workspace" | "modules" | "support";
  icon: SvgIconComponent;
  element: ReactElement;
  navigation: boolean;
};

export const appRouteManifest: readonly AppRouteDefinition[] = [
  { path: "/", label: "Início", group: "workspace", icon: HomeOutlinedIcon, element: <HomePage />, navigation: true },
  { path: "/paguai", label: "Paguai", group: "modules", icon: PaymentsOutlinedIcon, element: <FeaturePreparationPage feature="Paguai" scope="Solicitações, conferência e fluxo financeiro." />, navigation: true },
  { path: "/atendimento", label: "Atendimento", group: "modules", icon: SupportAgentOutlinedIcon, element: <FeaturePreparationPage feature="Atendimento" scope="Fila, tickets e histórico de atendimento." />, navigation: true },
  { path: "/kanban", label: "Kanban", group: "modules", icon: ViewKanbanOutlinedIcon, element: <FeaturePreparationPage feature="Kanban" scope="Quadros, cards e acompanhamento do trabalho." />, navigation: true },
  { path: "/pessoas", label: "Pessoas", group: "modules", icon: GroupsOutlinedIcon, element: <FeaturePreparationPage feature="Pessoas" scope="Diretório, ausências e visão de equipe." />, navigation: true },
  { path: "/ajuda", label: "Ajuda", group: "support", icon: HelpOutlinedIcon, element: <HelpPage />, navigation: true },
] as const;
```

Map `/` to an index route and strip the leading slash for child paths in one
helper inside `router.tsx`.

- [ ] **Step 5: Run route and auth tests**

```bash
npm --prefix apps/web test -- --run src/app/router.test.tsx src/app/feature-preparation-page.test.tsx src/app/protected-route.test.tsx src/features/auth/login-page.test.tsx
```

Expected: PASS.

- [ ] **Step 6: Commit the routing boundary**

```bash
git add apps/web/src/shared/routing apps/web/src/shared/ui/page-header.tsx apps/web/src/app/app-route-manifest.tsx apps/web/src/app/feature-preparation-page.tsx apps/web/src/app/feature-preparation-page.test.tsx apps/web/src/app/router.tsx apps/web/src/app/router.test.tsx apps/web/src/app/protected-route.tsx apps/web/src/features/auth/login-page.tsx apps/web/src/app/app-navigation.tsx
git commit -m "feat(web): unify routes and module destinations"
```

### Task 3: Improved Home using the route manifest

**Files:**

- Modify: `apps/web/src/app/home-page.tsx`
- Modify: `apps/web/src/app/app-shell.test.tsx`

**Interfaces:**

- Produces: attractive all-authorized Home with real company cards and linked preparation destinations.
- Consumes: `PageHeader`, session bootstrap companies, and app route manifest links.

- [ ] **Step 1: Write failing Home navigation tests**

```tsx
it("shows real preparation destinations without fake operational metrics", async () => {
  const user = userEvent.setup();
  const router = renderShell("/", await signedInGateway());

  expect(screen.queryByText(/pendências hoje|atividade da equipe/i)).not.toBeInTheDocument();
  await user.click(screen.getByRole("link", { name: /Paguai/ }));
  expect(router.state.location.pathname).toBe("/paguai");
});
```

- [ ] **Step 2: Run and observe that cards are not links**

```bash
npm --prefix apps/web test -- --run src/app/app-shell.test.tsx
```

Expected: FAIL because the module cards are still static.

- [ ] **Step 3: Refactor Home without fake metrics**

Use `PageHeader`, improve card hierarchy, and make each module card a real link
only because its preparation route now exists. Keep company cards derived from
`bootstrap.companies` and retain the no-company alert.

```tsx
const moduleCards = appRouteManifest.filter(
  (route) => route.group === "modules" && route.navigation,
);
```

Render links with an explicit status:

```tsx
<CardActionArea component={RouterLink} to={route.path}>
  <CardContent>
    <Typography component="h3" variant="h6">{route.label}</Typography>
    <Typography color="text.secondary">Módulo em preparação</Typography>
  </CardContent>
</CardActionArea>
```

- [ ] **Step 4: Run focused page tests**

```bash
npm --prefix apps/web test -- --run src/app/feature-preparation-page.test.tsx src/app/app-shell.test.tsx
```

Expected: PASS, including no-company and help-route behavior.

- [ ] **Step 5: Commit the Home slice**

```bash
git add apps/web/src/app/home-page.tsx apps/web/src/app/app-shell.test.tsx
git commit -m "feat(web): improve all-authorized home"
```

### Task 4: Split shell rendering into focused components

**Files:**

- Create: `apps/web/src/app/shell/app-header.tsx`
- Create: `apps/web/src/app/shell/navigation-list.tsx`
- Modify: `apps/web/src/app/app-shell.tsx`
- Modify: `apps/web/src/app/app-shell.test.tsx`

**Interfaces:**

- `AppHeader({ onOpenNavigation, isSigningOut, onSignOut })` renders toolbar only.
- `NavigationList({ label, onNavigate? })` consumes `appRouteManifest` grouped by route group.
- `AppShell` owns only mobile drawer state, session error, layout and `Outlet`.

- [ ] **Step 1: Strengthen shell tests before moving code**

Add assertions for grouped navigation and trigger state:

```tsx
expect(screen.getAllByText("Módulos").length).toBeGreaterThan(0);
const trigger = screen.getByRole("button", { name: "Abrir navegação" });
await user.click(trigger);
expect(trigger).toHaveAttribute("aria-expanded", "true");
expect(trigger).toHaveAttribute("aria-controls", "vinx-mobile-navigation");
```

- [ ] **Step 2: Run the test and observe missing ARIA state**

```bash
npm --prefix apps/web test -- --run src/app/app-shell.test.tsx
```

Expected: FAIL because the trigger does not expose the open state/control id.

- [ ] **Step 3: Extract `AppHeader` and `NavigationList`**

The header receives explicit state:

```tsx
<IconButton
  aria-controls={mobileNavigationOpen ? "vinx-mobile-navigation" : undefined}
  aria-expanded={mobileNavigationOpen}
  aria-label="Abrir navegação"
  onClick={onOpenNavigation}
>
  <MenuOutlinedIcon />
</IconButton>
```

The navigation groups the single manifest:

```tsx
for (const group of navigationGroups) {
  const items = appRouteManifest.filter(
    (route) => route.navigation && route.group === group.id,
  );
  // Render one subheader and list for this concrete group.
}
```

Set the mobile paper id through `slotProps.paper.id = "vinx-mobile-navigation"`.

- [ ] **Step 4: Reduce `AppShell` to composition**

Keep the skip link and error region in `AppShell`, then compose:

```tsx
<AppHeader
  isSigningOut={isSigningOut}
  mobileNavigationOpen={mobileNavigationOpen}
  onOpenNavigation={() => setMobileNavigationOpen(true)}
  onSignOut={() => void signOut()}
/>
<Drawer open variant="permanent">
  <NavigationList label="Navegação lateral" />
</Drawer>
<Drawer open={mobileNavigationOpen} onClose={closeMobileNavigation} variant="temporary">
  <NavigationList label="Navegação principal" onNavigate={closeMobileNavigation} />
</Drawer>
<Box component="main" id="conteudo-principal"><Outlet /></Box>
```

Do not create a generic slot API in this PR; later feature consumers will add
their concrete header/drawer components.

- [ ] **Step 5: Run shell, route, and format checks**

```bash
npm --prefix apps/web test -- --run src/app/app-shell.test.tsx src/app/router.test.tsx
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
```

Expected: PASS.

- [ ] **Step 6: Commit the shell split**

```bash
git add apps/web/src/app/app-shell.tsx apps/web/src/app/app-shell.test.tsx apps/web/src/app/shell
git commit -m "refactor(web): split workspace shell rendering"
```

### Task 5: Browser proof and durable documentation

**Files:**

- Modify: `apps/web/e2e/app-shell-home.spec.ts`
- Modify: `apps/web/e2e/visual-theme-login.spec.ts`
- Modify: `apps/web/src/app/help-page.tsx`
- Modify: `docs/manuals/frontend/app-shell-home.md`
- Modify: `docs/manuals/frontend/visual-preferences.md`
- Create: `docs/verification/frontend-workspace-shell-test-cases.md`

**Interfaces:**

- Produces: user-facing Portuguese guidance and public verification map.
- Consumes: final route labels, mode names, module preparation copy.

- [ ] **Step 1: Add failing Playwright coverage**

Add one scenario that signs in, visits all four destinations, returns Home,
switches light/dark, and verifies mobile drawer focus restoration:

```ts
for (const [name, path] of [
  ["Paguai", "/paguai"],
  ["Atendimento", "/atendimento"],
  ["Kanban", "/kanban"],
  ["Pessoas", "/pessoas"],
] as const) {
  await page.getByRole("link", { name }).first().click();
  await expect(page.getByRole("heading", { name })).toBeVisible();
  await expect(page.getByText("Módulo em preparação")).toBeVisible();
  await page.getByRole("link", { name: "Voltar ao Início" }).click();
}
```

Replace the obsolete `Tema: sistema` scenario in
`visual-theme-login.spec.ts` with an assertion that the menu exposes only
`Tema: claro` and `Tema: escuro`. Keep the existing media-change scenario for
explicit light mode; it proves that OS changes no longer override the chosen
theme.

Add a browser assertion on `/ajuda` that the appearance guidance names only
the light and dark themes and does not mention a system theme. This must fail
before updating the stale Help copy.

- [ ] **Step 2: Run the focused browser scenario**

```bash
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts visual-theme-login.spec.ts
```

Expected before final fixes: FAIL on the obsolete `Tema: sistema` selector or
the stale Help copy; the module-route scenario may already pass because those
routes were implemented test-first in Task 2.

- [ ] **Step 3: Update manuals and verification manifest**

Document only durable behavior:

```markdown
## Temas

Use **Tema: claro** ou **Tema: escuro**. A identidade da empresa não muda a
paleta do aplicativo e não representa autorização.

## Módulos em preparação

Paguai, Atendimento, Kanban e Pessoas possuem destinos navegáveis que explicam
o escopo futuro. Eles ainda não exibem dados nem ações operacionais.
```

Map each BDD to its Vitest/Playwright path and current status in
`docs/verification/frontend-workspace-shell-test-cases.md`.

Update `help-page.tsx` to describe exactly `tema claro ou escuro` while keeping
density guidance unchanged.

- [ ] **Step 4: Run the full focused frontend gate**

```bash
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
npm --prefix apps/web test -- --run
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts visual-theme-login.spec.ts
./scripts/verify-public.sh
```

Expected: all commands PASS.

- [ ] **Step 5: Commit documentation and browser proof**

```bash
git add apps/web/e2e/app-shell-home.spec.ts apps/web/e2e/visual-theme-login.spec.ts apps/web/src/app/help-page.tsx docs/manuals/frontend/app-shell-home.md docs/manuals/frontend/visual-preferences.md docs/verification/frontend-workspace-shell-test-cases.md
git commit -m "test(web): verify neutral workspace shell"
```

- [ ] **Step 6: Independent review checkpoint**

Run a read-only independent review against the PR diff. Fix only confirmed
findings, rerun affected focused tests, then push `codex/ui-workspace-shell` and
open a draft PR targeting `codex/issue-168-app-shell-home` while #194 remains
unmerged.
