# Verificação frontend do diretório de pessoas

| Cenário | Evidência automatizada |
| --- | --- |
| A rota real usa a tela de pessoas e módulos adiados permanecem adiados | `apps/web/src/app/app-route-manifest.test.tsx` — `wires Pessoas to the employee directory without changing deferred modules` |
| Sem empresa elegível, não há leitura nem reserva | `apps/web/src/features/people/people-page.test.tsx` — `does not call the gateway when no company is eligible` |
| A empresa é escolhida explicitamente e somente nomes são exibidos como texto | `apps/web/src/features/people/people-page.test.tsx` — `waits for explicit selection and only renders display names as text` |
| Formulário inválido não faz POST | `apps/web/src/features/people/people-page.test.tsx` — `validates locally before reserving` |
| POST usa antiforgery novo, contrato estrito e não expõe IDs | `apps/web/src/features/people/employee-directory-client.test.ts` — `gets a fresh antiforgery token before a single strict create request and hides identifiers` |
| Sucesso só recarrega após revalidar bootstrap | `apps/web/src/features/people/people-page.test.tsx` — `refreshes bootstrap before reloading the selected directory after success` |
| Falhas de acesso removem o estado e revalidam a sessão | `apps/web/src/features/people/people-page.test.tsx` — `clears the selected directory and revalidates bootstrap after unauthorized` e `clears the selected directory and revalidates bootstrap after forbidden` |
| Resultado ambíguo admite uma tentativa manual com o mesmo pedido | `apps/web/src/features/people/people-page.test.tsx` — `offers one explicit retry with the same request after an ambiguous result` |
| Concorrência não cria reservas duplicadas | `apps/web/src/features/people/people-page.test.tsx` — `disables duplicate reservation while the mutation is pending` e `synchronously ignores a second submit before React publishes pending state` |

Não há teste E2E nesta fatia. Paguai, Atendimento e Kanban permanecem fora do
comportamento do diretório; a asserção do manifesto protege suas rotas de
preparação.
