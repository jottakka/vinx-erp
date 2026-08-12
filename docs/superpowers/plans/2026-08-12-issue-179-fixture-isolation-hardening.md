# Isolamento do fixture PostgreSQL — plano de implementação

> **Para agentes:** use `superpowers:subagent-driven-development` ou
> `superpowers:executing-plans`, executando cada passo com checkbox.

**Objetivo:** Provar identidade por fixture, preservar causas de falha e testar
isolamento concorrente após #174.

**Arquitetura:** O fixture cria PostgreSQL com database/segredo exclusivos,
valida a identidade retornada pelo endpoint publicado e usa cleanup que mantém
erro primário. Teste concorrente usa dois containers reais; falhas de cleanup
usam seam restrito à infraestrutura de testes.

**Stack:** .NET 10, Testcontainers.PostgreSql 4.9.0, Npgsql 10.0.3, xUnit.

## Restrições globais

- Base: merge de #174; issue #179; branch `codex/issue-174-fixture-isolation-hardening`.
- Não alterar produção, provider, imagem, pacote, paralelismo global, #173 ou frontend.
- Sem retry, polling ou `Task.Delay` de readiness; hipótese não comprovada.
- Nunca expor senha ou connection string completa.
- A PR parte de #177 e tem estimativa adicional de 180–320 linhas manuais em
  quatro arquivos. O conjunto empilhado não é a base de revisão desta fatia;
  após merge de #177, a diff contra `main` deve permanecer até 320 linhas.
  Parar/redividir se exceder 800 linhas.

## Arquivos

- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixture.cs`.
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs`.
- Criar/modificar: infraestrutura test-only mínima caso o container real não permita injetar falha de descarte.
- Criar: `docs/verification/issue-179-fixture-isolation-hardening-test-cases.md`.
- Criar: este desenho e plano.

### Task 1: RED de identidade esperada e fixtures concorrentes

**Files:**

- Modificar: `FoundationPostgresFixtureTests.cs`.

**Interfaces:**

- Produzir uma leitura de identidade observável do fixture, sem senha.
- Estender `VerifyEndpointAsync` para receber identidade esperada por record
  interno, sem fazer endpoint ou senha públicos.

- [ ] **Step 1: Escrever RED de PostgreSQL válido, mas errado**

Criar configuração Npgsql de um PostgreSQL real já iniciado com database ou
usuário esperado deliberadamente diferente. Exigir `InvalidOperationException`
sanitizada antes de bootstrap. A mutação que o teste pega é remover comparação
de database/usuário.

- [ ] **Step 2: Rodar RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~VerifyEndpointAsync_rejects_postgresql_with_unexpected_identity'
```

Esperado: falha porque a implementação atual aceita qualquer PostgreSQL válido.

- [ ] **Step 3: Escrever RED concorrente**

Criar dois `FoundationPostgresFixture`, iniciar com `Task.WhenAll`, ler suas
identidades e exigir databases distintos, porta publicada distinta, pooling
desligado e descarte de ambos em `finally`. A mutação que pega é remover database
exclusivo ou comparar somente versão/porta.

- [ ] **Step 4: Rodar RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~Concurrent_fixtures_use_distinct_verified_postgresql_endpoints'
```

Esperado: falha porque ambos fixtures usam database default.

### Task 2: GREEN de identidade exclusiva

**Files:**

- Modificar: `FoundationPostgresFixture.cs`.
- Modificar: `FoundationPostgresFixtureTests.cs`.

- [ ] **Step 1: Configurar identidade antes do container**

Gerar database com prefixo seguro e GUID sem hífens. Gerar senha com
`RandomNumberGenerator`. Passar ambos a `PostgreSqlBuilder.WithDatabase` e
`WithPassword`; manter usuário fixo `postgres`. O record de endpoint guarda só
host, porta, database e username.

- [ ] **Step 2: Validar identidade semântica**

Trocar consulta por:

```sql
SELECT current_database(), current_user, inet_server_port(), version()
```

Exigir igualdade com `FoundationPostgresEndpoint.Database` e `.Username`, porta
interna `5432` e versão PostgreSQL. Erro contém só fase, host, porta e tipo.

- [ ] **Step 3: Rodar GREEN focal**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~VerifyEndpointAsync_rejects_postgresql_with_unexpected_identity|FullyQualifiedName~Concurrent_fixtures_use_distinct_verified_postgresql_endpoints|FullyQualifiedName~StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint'
```

Esperado: todos passam com PostgreSQL real e sem segredo no erro.

### Task 3: RED/GREEN de cleanup sem mascaramento

**Files:**

- Modificar: `FoundationPostgresFixture.cs` e `FoundationApiFactory.cs`.
- Modificar: teste de infraestrutura e criar double test-only mínima, se necessária.

- [ ] **Step 1: Escrever RED de falha dupla**

Usar seam interna test-only de container que lança falha primária durante
start/probe e falha no primeiro dispose. Exigir que resultado mantenha ambas as
exceções e que segundo dispose faça nova tentativa. Repetir contra
`FoundationApiFactory` com falha de migração/seed e cleanup, exigindo primária
primeiro e tentativa de todos recursos. A mutação que pega é marcar
`_disposed=true` antes de descartar, relançar somente cleanup ou descartar
silenciosamente falhas secundárias.

- [ ] **Step 2: Rodar RED**

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~StartAsync_preserves_primary_failure_when_cleanup_also_fails|FullyQualifiedName~DisposeAsync_retries_after_a_failed_container_cleanup'
```

Esperado: falha pelo comportamento atual.

- [ ] **Step 3: Implementar cleanup mínimo**

Definir `IFoundationPostgresContainer` interno com `StartAsync`, `Hostname`,
`GetMappedPublicPort`, `GetConnectionString` e `DisposeAsync`; adaptador real
encapsula `PostgreSqlContainer`, e construtor interno do fixture recebe a seam.
Definir `IFoundationFixtureLifecycle` interno com `RuntimeConnectionString`,
`StartAsync`, `MigrateAsync`, `CreateAdministratorDbContext` e `DisposeAsync`.
O construtor/entradas públicas continuam instanciando fixture real; a entrada
interna de factory recebe lifecycle controlado e delegates de descarte de host e
JWT. Centralizar em `CompleteAsync(Exception? primaryFailure, params
Func<ValueTask>[] cleanupSteps)`: tentar todos os cleanups, relançar primária se
não houver falha secundária e lançar `AggregateException` com primária primeiro
quando houver. Marcar descarte concluído só depois de sucesso, permitindo nova
tentativa após falha.

- [ ] **Step 4: Rodar GREEN focal**

Rodar mesmo filtro do Step 2, incluindo
`PrepareAsync_preserves_primary_failure_when_cleanup_also_fails`; esperado:
todos passam.

### Task 4: Manifesto e gate

- [ ] **Step 1: Mapear BDDs**

Criar manifesto curto com os cinco cenários e seus testes reais; marcar acesso
como N/A com motivo explícito.

- [ ] **Step 2: Repetir concorrência serialmente**

```bash
for run in 1 2 3 4 5; do
  dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~Concurrent_fixtures_use_distinct_verified_postgresql_endpoints'
done
```

Esperado: cinco execuções passam sem `SSLRequest response H`.

- [ ] **Step 3: Gate público**

```bash
dotnet format Vinx.Erp.sln whitespace --verify-no-changes --no-restore
./scripts/verify-public.sh
git diff --check origin/main...HEAD
```

Esperado: todos passam. Recalcular diff antes de publicar.
