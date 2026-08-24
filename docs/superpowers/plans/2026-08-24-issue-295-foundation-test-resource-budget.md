# Foundation Test Resource Budget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Limitar cada processo de `Vinx.Erp.Foundation.IntegrationTests` a duas collections xUnit concorrentes sem alterar isolamento, lifecycle ou produção.

**Architecture:** Um atributo compilado no assembly define `MaxParallelThreads = 2` e preserva `CollectionPerClass`, paralelismo habilitado e algoritmo conservador. Um teste sem Docker fixa o contrato; o runbook e o manifesto registram o limite por processo e a coexistência independente de Docker Desktop e Colima.

**Tech Stack:** .NET SDK 10.0.302, xUnit 2.9.3, xunit.runner.visualstudio 4.0.0, Testcontainers.PostgreSql 4.14.0 e `postgres:17.9-alpine`.

## Global Constraints

- Base e entrega: issue #295, branch `codex/issue-295-bound-testcontainers-concurrency`, PR única para `main`.
- Teto exato: duas collections paralelas por processo; processos e worktrees continuam independentes.
- Preservar PostgreSQL real, banco/usuário/senha/porta exclusivos por fixture e Ryuk habilitado.
- Não alterar `FoundationPostgresFixture`, `FoundationApiFactory`, timeouts, retries, imagem, pacote, Supabase, produção ou CI.
- Não adicionar lock, fila, semaphore, lease global, reuse, container compartilhado ou cleanup duplicado.
- Não ler, listar nem executar `quality/private`.
- Documentação em português; evidência transitória fica na issue/PR.
- Executar `./scripts/verify-public.sh` uma vez, após congelar o escopo e antes do push.

---

### Task 1: Fixar o orçamento do assembly com RED/GREEN

**Files:**
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/TestAssemblyConfigurationTests.cs`
- Create: `tests/public/Vinx.Erp.Foundation.IntegrationTests/TestAssemblyConfiguration.cs`

**Interfaces:**
- Consumes: `Xunit.CollectionBehaviorAttribute` fornecido por xUnit 2.9.3.
- Produces: atributo de assembly com `MaxParallelThreads = 2`; nenhum tipo de produção ou API pública.

- [ ] **Step 1: Escrever o teste RED do contrato do assembly**

```csharp
using System.Reflection;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class TestAssemblyConfigurationTests
{
    [Fact]
    public void Assembly_limits_parallel_test_collections_without_disabling_parallelism()
    {
        var configuration = Assert.Single(
            typeof(TestAssemblyConfigurationTests).Assembly
                .GetCustomAttributes<CollectionBehaviorAttribute>());

        Assert.Equal(2, configuration.MaxParallelThreads);
        Assert.False(configuration.DisableTestParallelization);
    }
}
```

Quebra detectada: remoção do teto volta a permitir `ProcessorCount` collections; desabilitar todo paralelismo também viola o desenho.

- [ ] **Step 2: Executar RED e confirmar a causa**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests' \
  --logger 'console;verbosity=normal'
```

Expected: FAIL em `Assert.Single` porque `main` não declara `CollectionBehaviorAttribute`; nenhuma chamada Docker.

- [ ] **Step 3: Adicionar a implementação mínima**

```csharp
using Xunit;

[assembly: CollectionBehavior(MaxParallelThreads = 2)]
```

- [ ] **Step 4: Executar GREEN e o teste focado sem rebuild**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests' \
  --logger 'console;verbosity=normal'
```

Expected: PASS 1/1, 0 falhas e 0 skips.

- [ ] **Step 5: Revisar o diff e commitar a unidade testável**

Run:

```bash
git diff --check
git diff -- tests/public/Vinx.Erp.Foundation.IntegrationTests
git add \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/TestAssemblyConfigurationTests.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/TestAssemblyConfiguration.cs
git commit -m 'test(infra): bound Foundation parallelism'
```

### Task 2: Documentar contrato verificável e operação local

**Files:**
- Create: `docs/verification/issue-295-foundation-test-resource-budget-test-cases.md`
- Modify: `docs/runbooks/local-development.md:294`

**Interfaces:**
- Consumes: atributo e teste da Task 1; diagnóstico durável da issue #295.
- Produces: matriz BDD curta e procedimento seguro para reconhecer duas VMs de containers.

- [ ] **Step 1: Criar o manifesto de verificação**

```markdown
# Casos testados — orçamento de recursos Foundation (#295)

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — assembly limita collections sem remover paralelismo | `TestAssemblyConfigurationTests.Assembly_limits_parallel_test_collections_without_disabling_parallelism` | Impedir retorno ao teto por `ProcessorCount` | coberto |
| Validação/erro — configuração ausente ou diferente de 2 falha | Mesmo teste, RED confirmado antes do atributo | Provar que o teste detecta remoção ou regressão do contrato | coberto |
| Acesso — N/A | Revisão de diff restrita a testes e documentação | Não existe usuário, tenant, capability ou rota neste contrato | N/A — infraestrutura do runner |
| Estado/exceções — lifecycle e cleanup continuam intactos | `FoundationPostgresFixtureTests` e `FoundationApiFactoryLifecycleTests` | Preservar falha primária, descarte e Ryuk sem cleanup duplicado | coberto |
| Concorrência — amostra causal não excede dois PostgreSQL por processo | Comando focado da verificação + `docker ps --filter label=org.testcontainers=true` | Confirmar o efeito real do atributo no runner | coberto manualmente com evidência na PR |
```

- [ ] **Step 2: Acrescentar o diagnóstico durável ao runbook**

Adicionar em `## Diagnóstico e parada`, antes do comando de parada do Supabase:

```markdown
Os testes `Vinx.Erp.Foundation.IntegrationTests` permitem no máximo duas
collections xUnit em paralelo por processo. Worktrees simultâneas permanecem
processos independentes e multiplicam esse teto; não execute gates redundantes
sem necessidade.

Docker Desktop e Colima mantêm VMs distintas. Se `docker context show` retornar
`desktop-linux`, consulte `colima status`. Execute `colima stop` somente depois
de confirmar que nenhuma outra worktree ou projeto depende dessa instância; o
repositório não para runtimes globais automaticamente.
```

- [ ] **Step 3: Validar documentação e tamanho**

Run:

```bash
git diff --check
git diff --stat origin/main...HEAD
git diff -- docs/verification/issue-295-foundation-test-resource-budget-test-cases.md docs/runbooks/local-development.md
```

Expected: sem whitespace inválido; documentação curta em português; nenhuma alteração fora do escopo.

- [ ] **Step 4: Commitar documentação**

Run:

```bash
git add \
  docs/verification/issue-295-foundation-test-resource-budget-test-cases.md \
  docs/runbooks/local-development.md
git commit -m 'docs(test): record container resource budget'
```

### Task 3: Reproduzir o efeito, executar o gate e revisar

**Files:**
- Verify only: all files changed since `origin/main`.

**Interfaces:**
- Consumes: implementação e documentação das Tasks 1–2.
- Produces: evidência focada, gate público completo e diff pronto para PR.

- [ ] **Step 1: Executar contratos de configuração, lifecycle e isolamento**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests|FullyQualifiedName~FoundationPostgresFixtureTests|FullyQualifiedName~FoundationApiFactoryLifecycleTests' \
  --logger 'console;verbosity=normal'
```

Expected: 0 falhas; no máximo dois PostgreSQL Testcontainers simultâneos.

- [ ] **Step 2: Executar a amostra causal da issue #295**

Run:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~GetHealth_ReturnsHealthy|FullyQualifiedName~Role_assignment_with_a_membership_from_another_company_is_rejected_by_the_database|FullyQualifiedName~Company_command_requires_an_exact_company_grant|FullyQualifiedName~Capability_evaluation_uses_transaction_local_context_and_does_not_leave_company_access_on_a_runtime_connection|FullyQualifiedName~Updating_a_mutable_entity_advances_its_row_version|FullyQualifiedName~Successful_response_contains_a_server_created_correlation_id' \
  --logger 'console;verbosity=minimal'
```

Durante a execução, observar em outro processo:

```bash
docker ps --filter label=org.testcontainers=true \
  --format '{{.Image}}\t{{.Label "org.testcontainers.session-id"}}'
```

Expected: 10/10 verdes; até dois `postgres:17.9-alpine` mais um Ryuk na sessão; zero recursos da sessão após encerrar.

- [ ] **Step 3: Congelar escopo e executar o gate público completo**

Run:

```bash
./scripts/verify-public.sh
```

Expected: exit 0 em instalação, lint, format, build, scripts Foundation e todos os projetos públicos; não executar novamente salvo correção de blocker.

- [ ] **Step 4: Recalcular tamanho e conferir contratos**

Run:

```bash
git diff --check origin/main...HEAD
git diff --stat origin/main...HEAD
git diff --numstat origin/main...HEAD
git status --short --branch
```

Expected: 4 arquivos não-teste, 2 arquivos de teste/configuração, entre 380 e
500 linhas manuais, zero geradas, sem arquivo inesperado ou alteração não
commitada.

- [ ] **Step 5: Solicitar revisão independente e consolidar uma correção**

Revisar `origin/main...HEAD` contra a issue #295 e o desenho, priorizando:

- contrato real do atributo xUnit v2;
- teste RED/GREEN honesto;
- limite por processo e ausência de promessa global;
- lifecycle/Ryuk intactos;
- documentação sem instrução destrutiva ou automática.

Corrigir findings Critical/Important em uma única rodada, repetir somente os testes afetados e, se o gate completo já estava verde, executar validação proporcional sem ampliar escopo.

- [ ] **Step 6: Preparar push e PR**

Confirmar branch, commits, base `main`, issue #295, evidência focada, gate público e revisão. Usar a descrição da PR para registrar medições transitórias e `Closes #295`; manter a worktree para feedback.
