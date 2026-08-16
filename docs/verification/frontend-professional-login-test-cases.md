# Mapa de verificação: login profissional

## Limite da evidência

Esta fatia prova superfície visual e jornada release com BFF simulado no
browser. A entrega #168 substitui a superfície temporária por Home em `/` e
mantém `/protected` como redirecionamento de compatibilidade. Não prova API,
banco, RLS, provider, token de acesso ou autorização fora do BFF simulado.

| BDD class | English scenario | Playwright test | Manual check |
|---|---|---|---|
| happy | Maria uses credential entry at 320 CSS pixels, selects dark mode and compact density, reaches Home once and has no horizontal overflow | `visual-theme-login.spec.ts`: `Scenario: Maria uses the real-session credential entry` | Select **Tema: escuro** and **Densidade: compacta**; enter with BFF simulado; confirm one `Início`, Alfa/Beta only, then repeat at 200% zoom |
| validation/error | Empty, whitespace-only or invalid credentials stay on `/login`, explain next action and return focus | `visual-theme-login.spec.ts`: `Scenario: Invalid credentials remain recoverable`; component assertions | Repeat each value and confirm alert and focused field; no API/provider claim |
| access | Anonymous `/protected` compatibility deep link returns to `/login` | `visual-theme-login.spec.ts`: `anonymous /protected deep link returns to /login`; `mock-session.spec.ts` (nome legado): `Scenario: Anonymous deep link returns safely to login` com BFF simulado | Open `/protected` in a fresh context |
| state | Sessão BFF simulada restaura após recarga; preferências visuais persistem independentemente | `mock-session.spec.ts` (nome legado): `reload restores the authenticated session through the BFF contract`; testes de preferências | Entrar, recarregar e confirmar Home; confirmar tema/densidade selecionados |
| concurrency | Repeated sign-in remains one deterministic session and navigation | Component coverage: `login-page.test.tsx` deferred repeated sign-in and `session-provider-real-session.test.tsx` | Click **Entrar** repeatedly during a pending entry; inspect that Home is entered once |
| visual/accessibility | Light, dark, system, forced-colors, keyboard-only and Axe checks preserve named controls and focus | `visual-theme-login.spec.ts`: `dark, light and emulated system modes expose semantic computed palette roles`; `keyboard-only login and forced colors retain named controls and focus lands on submit`; `login and the authenticated Home have no automated WCAG AA violations` | At 320 CSS px and 200% zoom, use keyboard to open the appearance menu; confirm no clipped controls or horizontal overflow |

Apesar do nome legado, `mock-session.spec.ts` usa BFF simulado por
`e2e/support/bff-session.ts`. Nenhum teste estabelece API ou provider real nem
persiste dados autenticados.
