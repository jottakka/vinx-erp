# Casos testados — conjunto autorizado por empresa

| Classe BDD / cenário | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso: Alfa/Beta filtrados e Gama preservada sem capability | `MembershipMapEndpointTests.Authorized_company_set_filters_exact_company_capability_without_losing_unfiltered_snapshot` | Provar snapshot sem filtro, conjunto exato e `capabilities=[]` | coberto |
| Validação/erros: requirement não-Company, ator ausente e contexto/DB ausente falham fechado | `MembershipMapEndpointTests.Non_company_or_empty_requirement_fails_closed_without_an_authorized_set`; `MembershipMapEndpointTests.Membership_map_rejects_an_anonymous_call`; `RlsPolicyTests.Runtime_query_without_company_context_returns_zero_company_rows`; `MembershipMapEndpointTests.Membership_map_discovery_failure_is_logged_with_safe_operational_context` | Impedir conjunto parcial, vazamento e log com IDs/SQL | coberto |
| Acesso: input forjado e ator/empresa incorretos não ampliam o conjunto | `Authorization.feature: Authorized company set ignores forged Gama input and preserves snapshot state`; `RlsPolicyTests.Runtime_authorized_company_context_reads_only_the_server_resolved_set`; `RlsPolicyTests.Concurrent_authorized_company_contexts_keep_Maria_and_Bruno_isolated` | Autoridade exclusiva do resolver e isolamento Maria/Bruno/Gama | coberto |
| Estado/exceções: revogação aparece no snapshot seguinte e contexto somente-leitura não escreve | `MembershipMapEndpointTests.Revoked_membership_is_absent_from_the_next_authorized_company_snapshot`; `RlsPolicyTests.Authorized_company_context_does_not_authorize_writes_without_exact_company_context` | Preservar `app.company_id`, `WITH CHECK` e negação de escrita | coberto |
| Concorrência: contexto transacional não cruza conexões ou atores e revogação em voo é consistente | `RlsSessionContextTests.Authorized_read_context_is_transaction_local_and_does_not_cross_pooled_requests`; `MembershipMapEndpointTests.Concurrent_revoke_does_not_change_an_in_flight_set_but_changes_the_next_read` | Limpar `SET LOCAL`, manter conjunto materializado estável e refletir revogação na próxima leitura | coberto |

Não há endpoint bootstrap, leitura de domínio, paginação, cache, UI ou MCP
nesta fatia; as provas públicas são internas ao Foundation e usam PostgreSQL
real. A verificação privada não faz parte deste manifesto.
