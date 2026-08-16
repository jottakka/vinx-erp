# Mapa de verificação: Atendimento/Kanban MVP

| Classe | Caso | Evidência |
| --- | --- | --- |
| API | Criação retorna `201` com coluna `novo`; replay igual retorna `200` sem duplicar. | `AtendimentoCardEndpointTests` |
| API | Mesmo requestId com payload diferente retorna `409 idempotency_conflict`. | `AtendimentoCardEndpointTests` |
| Integridade | Movimento com versão antiga retorna `409` e preserva coluna atual. | `AtendimentoCardEndpointTests` |
| Atribuição | Membership ativa da mesma empresa é atribuída, incrementa versão e faz replay estável. | `AtendimentoCardEndpointTests` |
| Web | `/atendimento` e `/kanban` usam empresa do bootstrap BFF, fila same-origin, criação, movimentação, atribuição e mensagem segura de falha/conflito. | `npm --prefix apps/web run build` |

Comandos executados nesta fatia:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter FullyQualifiedName~AtendimentoCardEndpointTests --no-restore
npm --prefix apps/web run build
```
