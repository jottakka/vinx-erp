# Casos de verificação: bootstrap autenticado do frontend

## Limite da evidência

Esta matriz cobre o cliente e estado em memória do contrato já publicado em
`GET /foundation/bootstrap`. A composição real e a jornada de navegador de
#117 estão mapeadas em `frontend-real-session-test-cases.md`.

| Classe BDD | Cenário em inglês | Unitário/componente | Playwright | Manual |
|---|---|---|---|---|
| happy | User enters with all authorized companies | `foundation-bootstrap-client.test.ts`: Alfa, Beta, IDs, scopes, flags e operador; `session-provider-bootstrap.test.tsx`: snapshot atual | Ver matriz da sessão real | Conferir **Todas as empresas autorizadas** após a entrada BFF |
| validation | Invalid bootstrap never becomes partial protected state | `foundation-bootstrap-client.test.ts`: JSON, scope e correlação inválidos; 503 não documentado | Ver matriz da sessão real | Indisponibilidade mostra tentativa segura, sem detalhe técnico |
| access | Capability and flag are not authorization | `foundation-bootstrap-client.test.ts`: scopes preservados por empresa; `protected-route.test.tsx`: conteúdo bloqueado sem bootstrap válido | Ver matriz da sessão real | Confirmar que comando futuro continua requerendo empresa e autorização do servidor |
| state | Revoked membership preserves another authorized company | `session-provider-bootstrap.test.tsx`: Alfa removida, Beta preservada e cache limpo; `query-client.test.ts`: mutação offline falha uma vez e não é retomada ao reconectar | Ver matriz da sessão real | Revalidar após revogação e confirmar somente a empresa revogada some |
| concurrency | Older bootstrap cannot restore stale access | `session-provider-bootstrap.test.tsx`: geração nova vence leitura antiga | Ver matriz da sessão real | Alternar conectividade sem restaurar acesso antigo |
