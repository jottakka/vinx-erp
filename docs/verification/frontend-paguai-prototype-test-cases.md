# Mapa de verificação: protótipo Paguai

O protótipo Paguai é uma superfície local de desenvolvimento. Não persiste
dados, não chama serviços de domínio e não substitui a rota de produto
`/paguai`.

| Classe BDD | Comportamento | Evidência automatizada |
| --- | --- | --- |
| Acesso | Em desenvolvimento, a Home mantém Paguai em `/paguai` e mostra somente a ação secundária disponível para `/prototipos/paguai`. | `app-shell.test.tsx`; `module-prototypes.spec.ts` |
| Limite de publicação | No preview de produção não há navegação Demonstração nem ação Abrir protótipo; links diretos de protótipo autenticados voltam para `/`. | `app-shell-home.spec.ts` |
| Rascunho | Nova solicitação exige empresa explícita; a escolha usa o combobox acessível e cria somente alteração local de demonstração. | `module-prototypes.spec.ts` |
| Conferência | Devolução exige categoria e motivo; uma devolução válida preserva razão local e devolve foco à solicitação selecionada. | `module-prototypes.spec.ts` |
| Financeiro | Confirmação permanece bloqueada até o comprovante demonstrativo chegar a `ready`. | `module-prototypes.spec.ts` |
| Acessibilidade | Tema claro/escuro, teclado, 390 CSS px sem rolagem horizontal e varredura Axe WCAG A/AA continuam utilizáveis. | `module-prototypes.spec.ts` |

Comandos de verificação:

```bash
npm --prefix apps/web run test:e2e -- e2e/app-shell-home.spec.ts --reporter=line
npm --prefix apps/web run test:e2e:prototypes -- --reporter=line
```
