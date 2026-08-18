# Casos de verificação: sessão real do frontend

## Limite da evidência

#117 compõe `BrowserSessionClient` e `FoundationBootstrapClient` no release.
O browser usa somente BFF de mesma origem, cookie de prova e estado em memória:
não recebe token de acesso ou refresh, não persiste sessão em storage e não
chama provider. Antiforgery é transitório por mutação. Mock só existe por
injeção explícita em teste ou desenvolvimento.

Os Playwright usam `e2e/support/bff-session.ts`, que intercepta o BFF no
browser. Eles comprovam composição e jornada release; não comprovam API ou
provider real.

| Classe BDD | Cenário | Vitest atual | Playwright atual | Manual |
|---|---|---|---|---|
| sucesso | `204` de login seguido de bootstrap válido libera somente empresas autorizadas | `session-provider-real-session.test.tsx`: `initial 401...post-sign-in bootstrap`; `browser-session-client.test.ts`: CSRF e `POST` | `mock-session.spec.ts`: entrada e reload BFF; `visual-theme-login.spec.ts`: `real-session credential entry`; `app-shell-home.spec.ts`: shell autenticado | Entrar com credenciais válidas; confirmar que a rota protegida só abre após bootstrap |
| validação/erro | Credencial inválida, `401` de bootstrap ou indisponibilidade mantêm rota segura sem detalhe do provider | `login-page.test.tsx`, `browser-session-client.test.ts`, `session-provider-real-session.test.tsx`: bootstrap `401` | `visual-theme-login.spec.ts`: `Invalid credentials remain recoverable`; indisponibilidade BFF fica no Vitest | Confirmar mensagem acionável, foco na senha e ausência de diagnóstico técnico |
| acesso | `401` inicial ou revalidação remove snapshot e cache antes de conteúdo protegido | `protected-route.test.tsx`; `session-provider-real-session.test.tsx`: `initial 401` | `mock-session.spec.ts`: deep link anônimo; `visual-theme-login.spec.ts`: `/protected` anônimo | Revalidar sessão revogada e confirmar que conteúdo protegido não aparece |
| estado/exceção | Logout confirmado, `DELETE 401` ou `503 identity_gate_unavailable` conclui anônimo; demais falhas entram em recuperação | `browser-session-client.test.ts`; `session-provider-real-session.test.tsx`: antiforgery indisponível, `DELETE 503`, nova saída explícita | `mock-session.spec.ts`: logout e reload pelo contrato BFF; exceções de logout ficam no Vitest | Sair; em falha de recuperação, confirmar que reconexão não repete logout |
| concorrência | Login, logout, refresh e navegação sobrepostos não restauram bootstrap antigo | `session-provider-real-session.test.tsx`: bootstrap antigo após logout; geração e abortamento de #90 | Sem cenário Playwright dedicado; corrida determinística fica no Vitest | Repetir ações durante conectividade instável; acesso obsoleto nunca reaparece |

## Adiamentos

#44 mantém fora MFA/TOTP, recência, recuperação de conta e fluxos adicionais
do provider. #79 mantém fora refresh, listagem e revogação individual de
sessões. #91 mantém fora ajuda in-app; esta fatia não altera esse conteúdo.
