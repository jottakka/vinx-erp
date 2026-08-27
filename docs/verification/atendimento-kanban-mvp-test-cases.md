# Mapa de verificação: Atendimento/Kanban MVP

| Classe | Caso | Evidência |
| --- | --- | --- |
| API | Criação retorna `201` com coluna `novo`; replay igual retorna `200` sem duplicar. | `AtendimentoCardEndpointTests` |
| API | Mesmo requestId com payload diferente retorna `409 idempotency_conflict`. | `AtendimentoCardEndpointTests` |
| API | Criação aceita somente anexo confirmado `service_desk_attachment` da mesma empresa e participantes ativos; resposta devolve anexo e IDs deduplicados/ordenados. | `AtendimentoCardEndpointTests`, `RequestModelValidationBoundaryTests` |
| Integridade | Movimento com versão antiga retorna `409` e preserva coluna atual. | `AtendimentoCardEndpointTests` |
| Atribuição | Membership ativa da mesma empresa é atribuída, incrementa versão e faz replay estável. | `AtendimentoCardEndpointTests` |
| Atribuição | Opções retornam somente `membershipId` e `displayName` das memberships ativas da empresa, após capability `.assign`. | `AtendimentoCardEndpointTests`, `AtendimentoCardSchemaTests` |
| Idempotência | O mesmo `requestId` pode mover cartões distintos; cada rota inclui `cardId` no escopo e no hash. | `AtendimentoCardEndpointTests` |
| Cancelamento | Cancelamento dedicado exige motivo, cria histórico para `cancelado`, audita `service_desk.ticket.cancel`, repete receipt e bloqueia cancel/move/assign distintos. | `AtendimentoCardEndpointTests`, `RequestModelValidationBoundaryTests` |
| Cancelamento/UI | Mover, atribuir e cancelar são capabilities independentes na UI: o cancelamento exige `service_desk.ticket.cancel` (escopo Company) e não deriva de `.move`. | `atendimento-page.test.tsx` (Vitest) |
| Concorrência/UI | Lock por `companyId:cardId` compartilhado por move/assign/cancel: mutação do mesmo cartão não inicia enquanto outra está pendente; cartão de outra empresa permanece utilizável. | `atendimento-page.test.tsx` (Vitest) |
| Concorrência/UI | Resposta atrasada de move nunca rebaixa a versão publicada: snapshot cancelado versão 3 permanece quando a resposta versão 2 chega depois. | `atendimento-page.test.tsx` (Vitest) |
| Web/contrato | Opções de atribuição falham fechadas: schema estrito array/objeto com UUID canônico e `displayName` não vazio; payload malformado retorna `null`. | `atendimento-client.test.ts` (Vitest) |
| Integridade | Dois movimentos concorrentes têm um único vencedor; o `409` retorna o estado pós-corrida e há um único histórico. | `AtendimentoCardEndpointTests` |
| Isolamento | Fila selecionada não retorna cartão de outra empresa e empresa inacessível não vaza detalhe. | `AtendimentoCardEndpointTests` |
| Autorização/auditoria | Capability ausente retorna `403`; ação auditada preserva `service_desk.ticket.*` e motivo separado. | `AtendimentoCardEndpointTests` |
| Auditoria transacional | Falha de `audit.append_event` devolve `503` e desfaz cartão e idempotência. | `AtendimentoCardEndpointTests` |
| Cookie BFF | Mutação com cookie sem CSRF falha; token BFF recém-obtido permite a mutação e respostas não são cacheáveis. | `AtendimentoCardEndpointTests` |
| RLS/migration | Runtime só lê participantes da empresa da transação e possui somente `SELECT/INSERT`; função de opções usa owner de autorização sem grant direto a usuários; downgrade recusa referências novas. | `AtendimentoCardSchemaTests`, `AtendimentoCardMigrationTests` |
| Web | `/atendimento` usa empresa explícita do bootstrap BFF, fila same-origin, criação, movimentação, cancelamento, atribuição, CSRF e estado visual isolado. | testes Vitest e build do web |

Comandos executados nesta fatia:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter 'FullyQualifiedName~AtendimentoCardEndpointTests|FullyQualifiedName~AtendimentoCardSchemaTests|FullyQualifiedName~AtendimentoCardMigrationTests|FullyQualifiedName~RequestModelValidationBoundaryTests' --no-restore
npm --prefix apps/web run build
```
