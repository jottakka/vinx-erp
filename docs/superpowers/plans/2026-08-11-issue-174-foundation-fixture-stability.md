# Estabilidade do fixture PostgreSQL Foundation — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use `superpowers:subagent-driven-development` (recomendado) ou `superpowers:executing-plans`, marcando cada passo com checkbox.

**Objetivo:** Verificar o PostgreSQL no endpoint publicado pelo container antes do bootstrap e garantir que falhas parciais de fixture/factory não contaminem execuções posteriores.

**Arquitetura:** O fixture constrói uma connection string administrativa explícita a partir de `Hostname` e `GetMappedPublicPort(5432)` depois de `StartAsync`. Antes do bootstrap, uma abertura Npgsql e consulta PostgreSQL real validam endpoint; as strings efêmeras desativam pooling. Falhas são sanitizadas e descartam o container; a factory descarta primeiro o host, depois o banco.

**Stack:** .NET 10, Testcontainers.PostgreSql 4.9.0, Npgsql 10.0.3, PostgreSQL 17.9 e xUnit.

## Restrições globais

- Base: `22bc7894f2c39421e9673f3680b101062888afd6` (`origin/main`); branch `codex/issue-174-foundation-fixture-stability`.
- Não mudar produção, confirmação documental, endpoint HTTP, RLS, imagem PostgreSQL, pacote ou paralelismo global; não iniciar nem ler `quality/private`.
- A prontidão é uma condição observável: host/porta publicados, conexão Npgsql e consulta `current_database(), inet_server_port(), version()`.
- A porta publicada é o alvo da conexão; `inet_server_port()` esperado é `5432`, a porta interna do PostgreSQL, e não a porta mapeada no host.
- Erros não podem conter senha, string de conexão completa, token, log completo do container ou conteúdo de dados.
- Usar `Pooling=false` em strings administrativa, migradora e runtime; não chamar `NpgsqlConnection.ClearAllPools`.
- Não inserir `Task.Delay`, retry ilimitado ou desabilitar paralelismo para esconder o problema.
- Estimativa: 5 arquivos não-teste e 300–500 linhas manuais, incluindo desenho, plano e manifesto; permanece abaixo dos limites do repositório.

## Estrutura de arquivos

- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixture.cs` — endpoint explicitamente derivado, prova semântica, diagnóstico sanitizado e cleanup.
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs` — BDD de sucesso, endpoint inválido e repetição de fixture.
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs` — cleanup em falha parcial e ordem de descarte.
- Criar: `docs/verification/issue-174-foundation-fixture-stability-test-cases.md` — manifesto BDD curto.
- Já criado: `docs/superpowers/specs/2026-08-11-issue-174-foundation-fixture-stability-design.md`.

---

### Task 1: Escrever o RED para endpoint publicado e strings efêmeras

**Files:**

- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs`

**Interfaces:**

- Consumir a API existente `StartAsync`, `OpenAdministratorConnectionAsync`, `MigratorConnectionString` e `RuntimeConnectionString`.
- Produzir o teste `StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint`.

- [ ] **Step 1: Escrever o teste de sucesso que falha hoje**

Adicionar o teste abaixo, antes de mudar o fixture:

```csharp
await using var fixture = new FoundationPostgresFixture();
await fixture.StartAsync();

await using var administrator = await fixture.OpenAdministratorConnectionAsync();
var administratorSettings = new NpgsqlConnectionStringBuilder(administrator.ConnectionString);
var migratorSettings = new NpgsqlConnectionStringBuilder(fixture.MigratorConnectionString);
var runtimeSettings = new NpgsqlConnectionStringBuilder(fixture.RuntimeConnectionString);
Assert.False(administratorSettings.Pooling);
Assert.False(migratorSettings.Pooling);
Assert.False(runtimeSettings.Pooling);

await using var command = new NpgsqlCommand(
    "SELECT current_database(), inet_server_port(), version()", administrator);
await using var reader = await command.ExecuteReaderAsync();
Assert.True(await reader.ReadAsync());
Assert.False(string.IsNullOrWhiteSpace(reader.GetString(0)));
Assert.Equal(5432, reader.GetInt32(1));
Assert.StartsWith("PostgreSQL", reader.GetString(2), StringComparison.Ordinal);
```

A quebra protegida é “um fixture descartável reutiliza conexão física de outro
container ou só confia na prontidão interna”. O código atual deve falhar nas três
asserções de `Pooling`, pois suas connection strings não o desativam.

- [ ] **Step 2: Rodar o RED**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint'
```

Expected: FAIL nas asserções `Pooling == false`, não por indisponibilidade de Docker.

- [ ] **Step 3: Escrever o cenário de protocolo incompatível**

Adicionar um `TcpListener` em loopback que aceita uma conexão e devolve o byte
`H` após o cliente enviar o `SSLRequest`. O teste deve chamar a nova API interna
`FoundationPostgresFixture.VerifyEndpointAsync` com uma
`NpgsqlConnectionStringBuilder` de `Host=127.0.0.1`, porta do listener,
`Database=postgres`, `Username=postgres`, senha de fixture e `Pooling=false`.

Exigir `InvalidOperationException` com `phase=endpoint-connect`,
`host=127.0.0.1`, a porta numérica e o tipo da exceção; exigir ausência da senha
do fixture. A falha protegida é “handshake não PostgreSQL alcança bootstrap ou
vaza string de conexão”.

- [ ] **Step 4: Rodar o RED do protocolo**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~VerifyEndpointAsync_rejects_a_non_postgresql_handshake'
```

Expected: antes do código do Task 2, a build indica que `VerifyEndpointAsync`
ainda não existe; depois de introduzir a assinatura interna, a implementação
inicial sem tratamento deve falhar por não produzir o diagnóstico sanitizado.
Não aceitar um teste que passe apenas por observar a exceção crua do Npgsql.

- [ ] **Step 5: Commit**

```bash
git add tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs
git commit -m "test: specify postgres fixture endpoint verification"
```

### Task 2: Verificar endpoint e garantir descarte seguro

**Files:**

- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixture.cs`
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs`

**Interfaces:**

- Produzir `internal static Task VerifyEndpointAsync(NpgsqlConnectionStringBuilder settings, string phase, CancellationToken cancellationToken)`.
- Produzir `internal sealed record FoundationPostgresEndpoint(string Host, int Port, string Database, string Username, bool Pooling)` sem senha.
- Manter `StartAsync()`, `OpenAdministratorConnectionAsync()`, `MigratorConnectionString` e `RuntimeConnectionString` como contratos existentes.

- [ ] **Step 1: Reexecutar os dois REDs**

Executar os comandos dos Tasks 1, Steps 2 e 4. Confirmar que o primeiro falha
por pooling e o segundo não tem ainda o diagnóstico do fixture; não mudar
produção antes dessa evidência.

- [ ] **Step 2: Implementar a prova semântica do endpoint**

Em `FoundationPostgresFixture`:

1. Depois de `await _container.StartAsync()`, ler `_container.Hostname` e
   `_container.GetMappedPublicPort(5432)`.
2. Criar `NpgsqlConnectionStringBuilder` administrativa com esses valores,
   database/usuário/senha da string do container e `Pooling = false`.
3. Validar host, porta positiva e `Pooling == false` antes da conexão.
4. Chamar `VerifyEndpointAsync` antes de `ExecuteBootstrapAsync`. A implementação
   abre uma conexão Npgsql real e consulta:

```sql
SELECT current_database(), inet_server_port(), version()
```

5. Rejeitar banco vazio, porta interna diferente de `5432` ou versão que não
   comece por `PostgreSQL`.
6. Em qualquer exceção não cancelada, lançar `InvalidOperationException`
   contendo somente `phase`, host, porta e `exception.GetType().Name`; preservar
   a exceção interna e nunca incluir `settings.ConnectionString`.

Fazer `BuildConnectionString` partir da administrativa já validada, trocar
usuário/senha e manter `Pooling=false`. `CreateAdministratorDbContext` e
`OpenAdministratorConnectionAsync` usam a string administrativa, não uma nova
chamada direta a `_container.GetConnectionString()`.

- [ ] **Step 3: Garantir lifecycle e factory cleanup**

Envolver `StartAsync` em `try/catch`: se início, verificação ou bootstrap falhar,
chamar `DisposeAsync` e relançar. Em cada método `Create*Async` da factory,
envolver início/migração/seed em `try/catch`, descartar a factory e relançar.

A ordem de `FoundationApiFactory.DisposeAsync` deve ser:

```csharp
await base.DisposeAsync();
_jwtAuthority?.Dispose();
await _fixture.DisposeAsync();
```

Usar `finally` quando necessário para que a falha de um descarte não impeça o
outro. Não introduzir pool global, lock global ou alteração do runner.

- [ ] **Step 4: Rodar o GREEN focal**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~FoundationPostgresFixtureTests|FullyQualifiedName~GetHealth_ReturnsHealthy'
```

Expected: PASS; a prova de endpoint passa, o listener `H` falha com diagnóstico
sanitizado e o health check ainda sobe/encerra host e banco na ordem correta.

- [ ] **Step 5: Commit**

```bash
git add tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixture.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationPostgresFixtureTests.cs
git commit -m "fix: stabilize postgres test fixture"
```

### Task 3: Registrar BDDs, repetir em série e validar escopo

**Files:**

- Criar: `docs/verification/issue-174-foundation-fixture-stability-test-cases.md`

**Interfaces:**

- Consumir os testes de endpoint, handshake e `HealthEndpointTests.GetHealth_ReturnsHealthy` do Task 2.

- [ ] **Step 1: Criar o manifesto BDD curto em português**

Usar tabela `Cenário/BDD | Teste ou caminho | Intenção | Situação`. Mapear sucesso, handshake incompatível, N/A de acesso, descarte/novo ciclo e concorrência. Para concorrência, apontar a repetição serial e a execução normal do projeto; não afirmar cobertura paralela se a evidência não a tiver.

- [ ] **Step 2: Executar repetição serial do fixture**

Run:

```bash
for run in 1 2 3 4 5; do
  dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
    --no-build \
    --filter 'FullyQualifiedName~FoundationPostgresFixtureTests'
done
```

Expected: cinco execuções passam, sem `SSLRequest response H`. Executar sozinho,
sem a suite de #173 em paralelo.

- [ ] **Step 3: Executar regressão pública do projeto de integração**

Run:

```bash
dotnet format whitespace --verify-no-changes
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-build
```

Expected: PASS; se houver falha em confirmação concorrente, registrar que é a
issue #173 e não alterar esse contrato nesta PR.

- [ ] **Step 4: Recalcular escopo**

Run:

```bash
git diff --stat origin/main...HEAD
git diff --check origin/main...HEAD
```

Expected: até 5 arquivos não-teste e até 500 linhas manuais; se exceder, parar e
abrir issue/PR filha antes do push.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-11-issue-174-foundation-fixture-stability-design.md \
  docs/superpowers/plans/2026-08-11-issue-174-foundation-fixture-stability.md \
  docs/verification/issue-174-foundation-fixture-stability-test-cases.md
git commit -m "docs: verify postgres fixture stability"
```

