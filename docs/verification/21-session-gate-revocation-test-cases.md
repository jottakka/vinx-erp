# Casos BDD — issue 21, revogação própria de sessões

## Escopo

`POST /foundation/identity/sessions/revoke-all` atende somente à própria
pessoa autenticada. Não recebe corpo, identificador de usuário, empresa ou
sessão. O endpoint lê exclusivamente o `ActorContext` que o gate estabelece.

Em repetição concorrente há duas janelas permitidas, ambas com o mesmo estado
final revogado e uma única auditoria: `204/204` quando ambas passam pelo gate
antes de a barreira avançar, e `204/401` quando uma chamada observa a barreira
no gate. O cenário determinístico
`Revoke_all_without_a_body_revokes_own_sessions_and_invalidates_the_old_proof`
faz uma chamada após o primeiro `204` e espera `401`.

| Classe BDD | Cenário | Evidência pública | Resultado esperado |
|---|---|---|---|
| gate válido | Dada prova válida e identidade conhecida, quando rota protegida é chamada, então gate admite uma vez, cria `ActorContext`, consulta provider uma vez e alcança handler | `IdentityGateEndpointTests.Protected_capabilities_admits_a_valid_proof_once_and_uses_the_internal_actor` | resposta protegida; ator interno; uma consulta ao provider |
| prova inválida | Dada prova ausente, expirada ou inválida, quando rota protegida é chamada, então gate falha fechada antes de admissão | `IdentityGateEndpointTests.Invalid_session_proof_returns_401_without_a_reason` | `401`, sem motivo, sessão ou handler |
| identidade desconhecida | Dada prova válida sem identidade interna, quando rota protegida é chamada, então gate não admite | `IdentityGateEndpointTests.Unknown_external_identity_returns_401_before_admission` | `401`, sem sessão nem handler |
| sessão revogada | Dada sessão revogada, quando rota protegida é chamada, então gate bloqueia antes do handler | `IdentityGateEndpointTests.Revoked_session_returns_401_before_capabilities_handler` | `401` |
| barreira | Dada prova emitida na barreira ou antes dela, quando rota protegida é chamada, então gate bloqueia | `IdentityGateEndpointTests.Proof_at_or_before_session_barrier_returns_401_before_capabilities_handler` | `401` |
| bypass anônimo | Dado health ou endpoint `AllowAnonymous`, quando chamado, então não consulta provider nem gate | `IdentityGateEndpointTests.Health_does_not_consult_the_identity_provider_or_session_gate`; `IdentityGateEndpointTests.Allow_anonymous_endpoint_skips_identity_provider_and_session_gate` | resposta anônima normal |
| indisponibilidade | Dada falha de resolução ou admissão, quando rota protegida é chamada, então não vaza detalhe interno | `IdentityGateEndpointTests.Resolution_database_failure_returns_only_the_safe_identity_gate_code`; `IdentityGateEndpointTests.Admission_database_failure_returns_only_the_safe_identity_gate_code` | `503 application/json` com somente `code: identity_gate_unavailable` |
| endpoint de revogação | Dadas sessões próprias admitidas, quando chama `revoke-all` sem corpo, então revoga próprias sessões e invalida prova antiga; sem autenticação, gate interrompe | `IdentityGateEndpointTests.Revoke_all_without_a_body_revokes_own_sessions_and_invalidates_the_old_proof`; `IdentityGateEndpointTests.Revoke_all_without_authentication_returns_401` | `204` vazio e depois `401`; ou `401` sem sessão/auditoria |
| revogação e transação | Dadas sessões ativas próprias, quando serviço revoga, então avança barreira, revoga todas e grava uma auditoria; sem mudança preserva barreira; falha de auditoria faz rollback e log seguro | `SelfSessionRevocationServiceTests.Revoke_all_revokes_every_active_own_session_advances_barrier_and_appends_one_audit`; `SelfSessionRevocationServiceTests.No_change_preserves_barrier_and_denies_an_unobserved_old_proof`; `SelfSessionRevocationServiceTests.Audit_failure_rolls_back_revocations_and_barrier`; `SelfSessionRevocationServiceTests.Audit_failure_logs_one_safe_structured_error` | `Revoked` ou `NoChange`; sem estado parcial; auditoria e log corretos |
| concorrência | Dadas revogações simultâneas, quando disputam mesma transição, então há uma auditoria de sucesso e um no-op; no endpoint são permitidos `204/204` ou `204/401` | `SelfSessionRevocationServiceTests.Concurrent_revocations_create_one_success_audit_and_one_no_change`; `IdentityGateEndpointTests.Concurrent_revoke_all_requests_preserve_one_transition_with_permitted_results` | estado final revogado; uma auditoria |
| migration, grants e Down | Dada migration de sessão, quando aplica, usa função `SECURITY DEFINER` estreita e grants mínimos; quando executa função, mantém timestamp único e no-op monotônico; quando faz Down, falha fechada com estado observável e remove função/grants somente em estado seguro | `SessionMigrationTests.Migration_creates_locked_session_state_with_the_runtime_minimum_grants`; `SessionMigrationTests.Migration_exposes_self_revocation_only_through_a_secure_definer_function`; `SessionMigrationTests.Runtime_function_revokes_active_sessions_with_one_database_timestamp_and_keeps_no_change_monotonic`; `SessionMigrationTests.Self_revocation_downgrade_fails_closed_when_observable_state_exists`; `SessionMigrationTests.Successful_self_revocation_downgrade_removes_function_and_owner_session_grants` | privilégios mínimos; função única; `Down` seguro |

## Limites mantidos

Listagem e revogação individual (#79), interface de sessões (#80),
administração de sessões (#81), MFA/AAL (#18), recuperação (#19) e
logout/banimento no provedor (#20) continuam fora desta entrega. Nenhuma
resposta retorna identificador externo ou detalhe de sessão.
