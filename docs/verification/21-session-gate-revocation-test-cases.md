# Casos BDD — issue 21, revogação própria de sessões

## Escopo

`POST /foundation/identity/sessions/revoke-all` atende somente à própria
pessoa autenticada. Não recebe corpo, identificador de usuário, empresa ou
sessão. O endpoint lê exclusivamente o `ActorContext` que o gate estabelece.
A operação mantém o mesmo estado final em repetição concorrente: uma chamada
que chega depois de a barreira avançar pode receber `401` no gate, antes do
endpoint, sem desfazer a revogação já concluída.

| Classe BDD | Cenário | Evidência pública | Resultado esperado |
|---|---|---|---|
| sucesso | Dado duas sessões próprias admitidas por provas válidas, quando a pessoa chama `revoke-all` sem corpo, então ambas são revogadas e a barreira avança | `IdentityGateEndpointTests.Revoke_all_without_a_body_revokes_own_sessions_and_invalidates_the_old_proof` | `204` vazio; uma auditoria global com `revoked_session_count: 2`; prova antiga recebe `401` |
| autorização | Dada chamada sem autenticação, quando chama `revoke-all`, então o gate interrompe antes do endpoint | `IdentityGateEndpointTests.Revoke_all_without_authentication_returns_401` | `401` vazio, sem sessão nem auditoria |
| indisponibilidade | Dada indisponibilidade da função transacional, quando chama `revoke-all`, então estado não muda | `IdentityGateEndpointTests.Revoke_all_database_failure_returns_only_the_safe_identity_gate_code` | `503 application/json` com objeto de uma única propriedade `code: identity_gate_unavailable` |
| concorrência | Dadas duas chamadas simultâneas da própria pessoa, quando a primeira avança a barreira antes de a outra passar pelo gate, então ambas preservam o mesmo estado final | `IdentityGateEndpointTests.Concurrent_revoke_all_requests_preserve_one_transition_when_a_late_request_loses_admission` | pelo menos uma resposta `204`; todas em `{204, 401}`; sessões revogadas; uma auditoria de sucesso |

## Limites mantidos

Listagem e revogação individual (#79), interface de sessões (#80),
administração de sessões (#81), MFA/AAL (#18), recuperação (#19) e
logout/banimento no provedor (#20) continuam fora desta entrega. Nenhuma
resposta retorna identificador externo ou detalhe de sessão.
