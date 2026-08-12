# BDDs de isolamento e lifecycle de fixtures

Manifesto durável da issue #179. Acesso é N/A: este escopo é infraestrutura de
testes, sem endpoint, principal, capability ou decisão de autorização.

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
| --- | --- | --- | --- |
| Dado database e usuário esperados, quando o endpoint é validado, então a conexão confirma a identidade exclusiva antes do bootstrap | `FoundationPostgresFixture.StartAsync` e `StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint` | Impedir bootstrap em PostgreSQL diferente | Coberto |
| Dado database ou usuário esperado incorreto, quando a fixture valida a identidade, então falha com diagnóstico sanitizado | `VerifyEndpointAsync_rejects_postgresql_with_unexpected_database` e `VerifyEndpointAsync_rejects_postgresql_with_unexpected_username` | Nunca expor senha ou string de conexão | Coberto |
| Dada falha primária e cleanup do fixture também falho, quando o início termina, então ambas as causas permanecem identificáveis e o descarte pode ser tentado novamente | `StartAsync_preserves_primary_failure_when_cleanup_also_fails` e `DisposeAsync_retries_after_a_failed_container_cleanup` | Evitar mascaramento e estado de descarte irrecuperável | Coberto |
| Dada falha de migração ou seed e falhas de host, JWT e fixture, quando a factory prepara o ciclo, então preserva a causa primária e tenta todos os recursos | `PrepareAsync_preserves_primary_failure_when_all_cleanups_also_fail` e `CreateWithAuthenticatedIdentityAsync_preserves_seed_failure_when_all_cleanups_also_fail` | Evitar bootstrap parcial sem cleanup completo | Coberto |
| Dadas falhas em todos os recursos de descarte, quando `DisposeAsync` termina, então tenta cada recurso e agrega as causas | `DisposeAsync_attempts_all_cleanups_when_each_fails` | Não abandonar recursos posteriores | Coberto |
| Dadas duas fixtures reais, quando iniciam e descartam em paralelo, então usam identidades isoladas sem cruzamento | `Concurrent_fixtures_use_distinct_verified_postgresql_endpoints` | Provar isolamento concorrente com Testcontainers real | Coberto; repetição serial é gate da PR |
| Acesso | N/A | Infraestrutura de testes não decide acesso | N/A |

Não cobre endpoints, autenticação de produção, RLS, autorização, provider ou
dados persistidos fora das fixtures. Evidências transitórias e segredos não são
versionados.
