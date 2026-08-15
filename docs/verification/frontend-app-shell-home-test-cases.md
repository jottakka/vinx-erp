# Mapa de verificação: shell, Home e Ajuda

## Limite da evidência

Esta entrega apresenta a primeira experiência autenticada usando a sessão mockada de desenvolvimento. Ela não prova autenticação de produção, API, banco, autorização servidor, dados de domínio, conectividade offline ou módulos de negócio. Os destinos de módulos em preparação foram separados em [#193](https://github.com/jottakka/vinx-erp/issues/193) após a reestimativa de tamanho.

| Classe BDD | Cenário em inglês | Evidência automatizada | Verificação manual |
| --- | --- | --- | --- |
| Happy | Maria signs in and reaches a professional all-authorized home | `mock-session.spec.ts`: `Scenario: Maria signs in and reaches a professional all-authorized home`; `AppShell`: all-authorized Home | Entrar com `maria`; confirmar Início, Alfa e Beta, sem Gama nem identificadores técnicos |
| Validation/error | Protected startup has an accessible loading state; a failed logout remains recoverable | `protected-route.test.tsx`: accessible pending state; `AppShell`: recoverable logout failure | Iniciar com bootstrap pendente no ambiente de teste; provocar falha de saída e confirmar que a Home continua aberta com alerta |
| Access | Anonymous deep link returns safely to login | `router.test.tsx`: anonymous Help link; `mock-session.spec.ts`: `Scenario: Anonymous deep link returns safely to login` | Abrir `/`, `/ajuda` e `/protected` sem sessão; entrar e confirmar retorno apenas para rota interna conhecida |
| State | Unknown, external or query-bearing return path is rejected; successful logout returns to login | `login-page.test.tsx`: external return path; `router.test.tsx`: query-bearing protected path and successful logout; `mock-session.spec.ts`: `Scenario: Unknown or query-bearing return path is rejected` | Tentar caminho desconhecido, URL externa ou query como retorno; confirmar que a entrada vai para Início |
| Concurrency | Mobile drawer opens/closes predictably and navigation occurs once | `AppShell`: Escape/focus/one transition; `app-shell-home.spec.ts`: mobile drawer scenario | Em 320 CSS px, abrir o menu pelo teclado, pressionar Escape, confirmar foco no acionador e abrir Ajuda uma vez |
| Accessibility/visual | Semantic theme, forced-colors, landmarks and automated WCAG AA remain usable | `app-shell-home.spec.ts`: forced colors and Axe; `visual-theme-login.spec.ts`: theme/density/forced-colors | Validar claro, escuro, forced-colors, 320 CSS px e zoom de 200%; confirmar foco, contraste, ausência de corte e de rolagem horizontal |

## Comandos executados durante a entrega

```bash
npm --workspace web test -- --run
npm --workspace web run build
npm --workspace web run test:e2e -- e2e/mock-session.spec.ts e2e/visual-theme-login.spec.ts e2e/app-shell-home.spec.ts --reporter=line
```
