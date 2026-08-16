# Casos de verificação: bootstrap autenticado do frontend

## Limite da evidência

Esta matriz cobre o cliente e estado em memória do contrato já publicado em
`GET /foundation/bootstrap`. A jornada com login BFF real e Playwright depende
da composição da sessão em #117; portanto não há evidência E2E honesta nesta
fatia.

| Classe BDD | Cenário em inglês | Unitário/componente | Playwright | Manual |
|---|---|---|---|---|
| happy | User enters with all authorized companies | `foundation-bootstrap-client.test.ts`: Alfa, Beta, IDs, scopes, flags e operador; `session-provider-bootstrap.test.tsx`: snapshot atual | Não aplicável antes de #117 | Conferir **Todas as empresas autorizadas** após a sessão real estar composta |
| validation | Invalid bootstrap never becomes partial protected state | `foundation-bootstrap-client.test.ts`: JSON, scope e correlação inválidos; 503 não documentado | Não aplicável antes de #117 | Indisponibilidade mostra tentativa segura, sem detalhe técnico |
| access | Capability and flag are not authorization | `foundation-bootstrap-client.test.ts`: scopes preservados por empresa; `protected-route.test.tsx`: conteúdo bloqueado sem bootstrap válido | Não aplicável antes de #117 | Confirmar que comando futuro continua requerendo empresa e autorização do servidor |
| state | Revoked membership preserves another authorized company | `session-provider-bootstrap.test.tsx`: Alfa removida, Beta preservada e cache limpo; `query-client.test.ts`: mutação offline falha uma vez e não é retomada ao reconectar | Não aplicável antes de #117 | Revalidar após revogação e confirmar somente a empresa revogada some |
| concurrency | Older bootstrap cannot restore stale access | `session-provider-bootstrap.test.tsx`: geração nova vence leitura antiga | Não aplicável antes de #117 | Alternar conectividade somente depois da jornada real da #117 |
