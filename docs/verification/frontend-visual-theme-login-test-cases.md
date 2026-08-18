# Casos de verificação: tema visual e login do frontend

## Limite da evidência

Esta fatia prova apresentação semântica, preferências visuais e entrada release
via BFF simulado. Não prova API, banco, RLS, provider ou autorização fora da
simulação.

| BDD class | English scenario | Vitest | Playwright | Manual check |
|---|---|---|---|---|
| happy | Authorized user enters `/` with Alfa and Beta only | `login-page.test.tsx`: successful credential entry; bootstrap projection tests | `visual-theme-login.spec.ts`: `successful Maria entry reaches all-authorized home with only Alfa and Beta` | Enter with BFF simulado; confirm Alfa/Beta and no Gama |
| validation/error | Invalid credentials remain public and expose actionable feedback | `login-page.test.tsx`: role `alert` and no protected content | `visual-theme-login.spec.ts`: `Invalid credentials remain recoverable` | Enter invalid credentials and inspect alert |
| access | An anonymous protected deep link returns to `/login` | `protected-route.test.tsx`: anonymous redirect | `visual-theme-login.spec.ts`: protected deep link | Open `/protected` in a fresh browser context |
| state | Reload preserves visual preferences; sessão BFF é revalidada separadamente | `visual-preferences.test.tsx`: explicit mode and compact density restore | `visual-theme-login.spec.ts`: reload mode and density; `mock-session.spec.ts` (nome legado): reload BFF autenticado | Select mode/density, reload, and confirm preferences; session follows BFF |
| concurrency | Repeated submit remains disabled during one deferred sign-in and navigates once | `login-page.test.tsx`: deferred repeated sign-in | Not applicable: completion order is deterministic component behavior | Submit repeatedly while the first action is pending |
| visual semantics | Light, dark, system, explicit-light-after-system-change and company accents use semantic roles | `company-badge.test.tsx`: local/neutral resolver and accessible badge; theme tests cover semantic palettes | `visual-theme-login.spec.ts`: computed palette roles and explicit light behavior | Change mode and inspect readable contrast at desktop/mobile widths |
| responsive/accessibility | Mobile, keyboard-only and forced-colors users retain named controls and focus | `login-page.test.tsx`: 320px component semantics | `visual-theme-login.spec.ts`: mobile overflow, keyboard-only forced colors, and Axe WCAG AA scans | Use only keyboard at 320 CSS px and test focus visibility |
