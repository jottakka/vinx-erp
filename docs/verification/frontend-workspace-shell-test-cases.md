# Mapa de verificação: shell neutro do workspace

## Limite da evidência

Esta fatia valida navegação autenticada, módulos em preparação, preferências
visuais e ajuda. Não valida dados de módulos, ações operacionais, autorização
de servidor, API ou persistência de sessão.

| Classe BDD | Cenário | Vitest | Playwright | Status atual |
| --- | --- | --- | --- | --- |
| Happy | Usuário autorizado visita Paguai, Atendimento e Pessoas e volta ao Início | `app-shell.test.tsx`: links de módulos (Kanban ausente); `feature-preparation-page.test.tsx`: página e retorno | `app-shell-home.spec.ts`: cenário de destinos preparados | Coberto |
| Access | Visitante anônimo volta somente para um caminho seguro após entrar | `router.test.tsx`: deep links e rejeição de query | `visual-theme-login.spec.ts`: deep link protegido | Coberto |
| State | Tema claro e escuro permanecem escolha explícita | `visual-preferences.test.tsx`: modo explícito; `appearance-menu.test.tsx`: seleção | `visual-theme-login.spec.ts`: tema claro após mudança do sistema | Coberto |
| Repetição/concorrência | Alterações repetidas preservam seleção final e navegação mobile ocorre uma vez | `appearance-menu.test.tsx`: alterações repetidas; `app-shell.test.tsx`: uma transição | `app-shell-home.spec.ts`: drawer mobile | Coberto |
| Validation/error | Preferência visual legada não é oferecida | `appearance-menu.test.tsx`: ausência de `Tema: sistema` | `visual-theme-login.spec.ts`: somente dois temas | Coberto |
| Accessibility/visual | Drawer mobile fecha por teclado e devolve foco ao acionador | `app-shell.test.tsx`: foco do drawer | `app-shell-home.spec.ts`: cenário mobile | Coberto |
| Documentation | Ajuda descreve somente tema claro ou escuro; densidade permanece disponível | Não aplicável: orientação humana | `visual-theme-login.spec.ts`: orientação em `/ajuda` | Coberto |

## Comandos públicos

```bash
npm --prefix apps/web run lint
npm --prefix apps/web run format:check
npm --prefix apps/web test -- --run
npm --prefix apps/web run build
npm --prefix apps/web run test:e2e -- app-shell-home.spec.ts visual-theme-login.spec.ts
./scripts/verify-public.sh
```
