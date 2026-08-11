# Mapa de verificação: login profissional mockado

## Limite da evidência

Esta fatia prova a superfície visual e a entrada mockada temporária em
`/protected`. Não prova autenticação de produção, API, banco, RLS, provider,
token ou autorização fora da fixture.

| BDD class | English scenario | Playwright test | Manual check |
|---|---|---|---|
| happy | Maria uses the professional development entry at 320 CSS pixels, selects dark mode, reaches `/protected` once, and has no horizontal overflow | `visual-theme-login.spec.ts`: `Scenario: Maria uses the professional development entry` | Run with `maria`; confirm one `Protótipo autenticado`, Alfa/Beta only, then repeat at 200% zoom |
| validation/error | An empty, whitespace-only or unavailable development profile stays on `/login`, explains the next action, and returns focus to the field | `visual-theme-login.spec.ts`: `Scenario: Invalid development profile remains recoverable`; component error assertions | Repeat each value and confirm the alert and focused profile field; no production-auth claim |
| access | An anonymous `/protected` deep link returns to `/login` | `visual-theme-login.spec.ts`: `anonymous /protected deep link returns to /login`; `mock-session.spec.ts`: `redirects an anonymous protected deep link to login` | Open `/protected` in a fresh context |
| state | Reload removes the mock session while selected theme and density restore visually | `visual-theme-login.spec.ts`: `reload returns to /login while the selected visual mode restores`; `compact density changes the selected visual density and survives reload without a mock session`; light/dark/system tests | Select each theme and density from **Abrir preferências visuais**, reload, and confirm public login remains |
| concurrency | Repeated sign-in remains one deterministic mock session and navigation | Component coverage: `login-page.test.tsx` deferred repeated sign-in and `mock-session-gateway.test.ts` overlapping operations | Click **Entrar** repeatedly during a pending entry; inspect that the prototype is entered once |
| visual/accessibility | Light, dark, system, forced-colors, keyboard-only and Axe checks preserve named controls and focus | `visual-theme-login.spec.ts`: `dark, light and emulated system modes expose semantic computed palette roles`; `keyboard-only login and forced colors retain named controls and focus lands on submit`; `login and the temporary protected entry have no automated WCAG AA violations` | At 320 CSS px and 200% zoom, use keyboard to open the appearance menu; confirm no clipped controls or horizontal overflow |

The `mock-session.spec.ts` tests retain the old evidence boundary: they cover
only the in-memory development fixture, protected-route behavior, logout and
reload. No test establishes production authentication or persists authenticated
data.
