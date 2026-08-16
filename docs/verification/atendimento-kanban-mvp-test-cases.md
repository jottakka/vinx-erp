# Mapa de verificação: Atendimento/Kanban MVP

| Classe | Caso | Evidência |
| --- | --- | --- |
| API | Criação retorna `201` com coluna `novo`; replay igual retorna `200` sem duplicar. | `AtendimentoCardEndpointTests` |
| API | Mesmo requestId com payload diferente retorna `409 idempotency_conflict`. | `AtendimentoCardEndpointTests` |
| Integridade | Movimento com versão antiga retorna `409` e preserva coluna atual. | `AtendimentoCardEndpointTests` |
| Atribuição | Membership ativa da mesma empresa é atribuída, incrementa versão e faz replay estável. | `AtendimentoCardEndpointTests` |
| Idempotência | O mesmo `requestId` pode mover cartões distintos; cada rota inclui `cardId` no escopo e no hash. | `AtendimentoCardEndpointTests` |
| Integridade | Dois movimentos concorrentes têm um único vencedor; o `409` retorna o estado pós-corrida e há um único histórico. | `AtendimentoCardEndpointTests` |
| Isolamento | Fila selecionada não retorna cartão de outra empresa e empresa inacessível não vaza detalhe. | `AtendimentoCardEndpointTests` |
| Autorização/auditoria | Capability ausente retorna `403`; ação auditada preserva `service_desk.ticket.*` e motivo separado. | `AtendimentoCardEndpointTests` |
| Auditoria transacional | Falha de `audit.append_event` devolve `503` e desfaz cartão e idempotência. | `AtendimentoCardEndpointTests` |
| Cookie BFF | Mutação com cookie sem CSRF falha; token BFF recém-obtido permite a mutação e respostas não são cacheáveis. | `AtendimentoCardEndpointTests` |
| RLS/migration | Runtime só lê a empresa da transação e não possui DELETE; catálogo preserva UUID legado, falha para colisão canônica e recusa downgrade referenciado. | `AtendimentoCardSchemaTests`, `AtendimentoCardMigrationTests` |
| Web | `/atendimento` e `/kanban` usam empresa do bootstrap BFF, fila same-origin, criação, movimentação, atribuição, CSRF e estado visual isolado. | testes Vitest e build do web |

Comandos executados nesta fatia:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter FullyQualifiedName~AtendimentoCardEndpointTests --no-restore
npm --prefix apps/web run build
```
