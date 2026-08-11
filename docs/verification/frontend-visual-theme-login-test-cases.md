# Casos de verificação: tema visual e login do frontend

## Limite da evidência

Esta fatia prova a apresentação semântica, os controles de preferência visual,
o login mockado e a entrada protegida temporária. Não prova autenticação de
produção, API, banco, RLS, provider ou autorização fora da fixture; essas
evidências pertencem às issues #114 e #115.

| BDD class | English scenario | Vitest | Playwright | Manual check |
|---|---|---|---|---|
| happy | Authorized Maria enters `/protected` with Alfa and Beta only | `login-page.test.tsx`: successful prototype sign-in; existing mock gateway projection tests | `visual-theme-login.spec.ts`: successful Maria entry | Enter with `maria`; confirm Alfa/Beta and no Gama |
| validation/error | An unavailable profile remains public and exposes actionable feedback | `login-page.test.tsx`: role `alert` and no protected content | `visual-theme-login.spec.ts`: invalid profile stays on `/login` | Enter an unknown profile and inspect the alert |
| access | An anonymous protected deep link returns to `/login` | `protected-route.test.tsx`: anonymous redirect | `visual-theme-login.spec.ts`: protected deep link | Open `/protected` in a fresh browser context |
| state | Reload removes the mock session while visual preferences restore safely | `visual-preferences.test.tsx`: explicit mode and compact density restore | `visual-theme-login.spec.ts`: reload mode and density scenarios | Select mode/density, reload, and confirm login remains public |
| concurrency | Repeated submit remains disabled during one deferred sign-in and navigates once | `login-page.test.tsx`: deferred repeated sign-in | Not applicable: completion order is deterministic component behavior | Submit repeatedly while the first action is pending |
| visual semantics | Light, dark, system, explicit-light-after-system-change and company accents use semantic roles | `company-badge.test.tsx`: local/neutral resolver and accessible badge; theme tests cover semantic palettes | `visual-theme-login.spec.ts`: computed palette roles and explicit light behavior | Change mode and inspect readable contrast at desktop/mobile widths |
| responsive/accessibility | Mobile, keyboard-only and forced-colors users retain named controls and focus | `login-page.test.tsx`: 320px component semantics | `visual-theme-login.spec.ts`: mobile overflow, keyboard-only forced colors, and Axe WCAG AA scans | Use only keyboard at 320 CSS px and test focus visibility |
