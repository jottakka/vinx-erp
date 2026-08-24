# Plano de implementação do orçamento de recursos Foundation

> **Para agentes executores:** SUB-SKILL OBRIGATÓRIA: usar
> `superpowers:subagent-driven-development` (recomendado) ou
> `superpowers:executing-plans` para executar o plano tarefa por tarefa. As
> etapas usam checkboxes (`- [ ]`) para acompanhamento.

**Objetivo:** Limitar cada processo de
`Vinx.Erp.Foundation.IntegrationTests` a duas collections xUnit concorrentes
e selecionar verificações locais proporcionais ao impacto, sem alterar
isolamento, ciclo de vida, produção ou CI.

**Arquitetura:** Um atributo compilado no assembly define
`MaxParallelThreads = 2` e preserva `CollectionPerClass`, paralelismo habilitado
e algoritmo conservador. Um teste sem Docker fixa o contrato; o runbook e o
manifesto registram o limite por processo e a coexistência independente de
Docker Desktop e Colima. `AGENTS.md` e o runbook público definem fases, matriz de
impacto e gatilhos para que gates pesados ocorram somente em momentos
estratégicos.

**Stack técnico:** .NET SDK 10.0.302, xUnit 2.9.3,
xunit.runner.visualstudio 4.0.0, Testcontainers.PostgreSql 4.14.0 e
`postgres:17.9-alpine`.

## Restrições globais

- Base e entrega: issue #295, branch `codex/issue-295-bound-testcontainers-concurrency`, PR única para `main`.
- Teto exato: duas collections paralelas por processo; processos e worktrees continuam independentes.
- Preservar PostgreSQL real, banco/usuário/senha/porta exclusivos por fixture e Ryuk habilitado.
- Não alterar `FoundationPostgresFixture`, `FoundationApiFactory`, timeouts,
  retentativas, imagem, pacote, Supabase, produção ou CI.
- Não adicionar lock, fila, semáforo, lease global, reutilização, container compartilhado ou descarte duplicado.
- Não ler, listar nem executar `quality/private`.
- Documentação em português; evidência transitória fica na issue/PR.
- O CI continua executando o gate completo da PR; não alterar `.github/workflows/ci.yml` nem adicionar seletor por caminho.
- Gate local completo roda no máximo uma vez após o congelamento de escopo e somente com gatilho estratégico.
- A extensão documental posterior ao gate já verde usa validação proporcional e não repete `verify-public.sh`.

---

### Tarefa 1: Fixar o orçamento do assembly com RED/GREEN

**Arquivos:**
- Criar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/TestAssemblyConfigurationTests.cs`
- Criar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/TestAssemblyConfiguration.cs`

**Interfaces:**
- Consome: `Xunit.CollectionBehaviorAttribute` fornecido por xUnit 2.9.3.
- Produz: atributo de assembly com `MaxParallelThreads = 2`; nenhum tipo de produção ou API pública.

- [ ] **Etapa 1: Escrever o teste RED do contrato do assembly**

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

- [ ] **Etapa 2: Executar RED e confirmar a causa**

Executar:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests' \
  --logger 'console;verbosity=normal'
```

Esperado: FAIL em `Assert.Single` porque `main` não declara `CollectionBehaviorAttribute`; nenhuma chamada Docker.

- [ ] **Etapa 3: Adicionar a implementação mínima**

```csharp
using Xunit;

[assembly: CollectionBehavior(MaxParallelThreads = 2)]
```

- [ ] **Etapa 4: Executar GREEN e o teste focado**

Executar:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests' \
  --logger 'console;verbosity=normal'
```

Esperado: PASS 1/1, 0 falhas e 0 skips.

- [ ] **Etapa 5: Revisar o diff e commitar a unidade testável**

Executar:

```bash
git diff --check
git diff -- tests/public/Vinx.Erp.Foundation.IntegrationTests
git add \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/TestAssemblyConfigurationTests.cs \
  tests/public/Vinx.Erp.Foundation.IntegrationTests/TestAssemblyConfiguration.cs
git commit -m 'test(infra): bound Foundation parallelism'
```

### Tarefa 2: Documentar contrato verificável e operação local

**Arquivos:**
- Criar: `docs/verification/issue-295-foundation-test-resource-budget-test-cases.md`
- Modificar: `docs/runbooks/local-development.md:294`

**Interfaces:**
- Consome: atributo e teste da Tarefa 1; diagnóstico durável da issue #295.
- Produz: matriz BDD curta e procedimento seguro para reconhecer duas VMs de containers.

- [ ] **Etapa 1: Criar o manifesto de verificação**

```markdown
# Casos testados — orçamento de recursos Foundation (#295)

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — assembly limita collections sem remover paralelismo | `TestAssemblyConfigurationTests.Assembly_limits_parallel_test_collections_without_disabling_parallelism` | Impedir retorno ao teto por `ProcessorCount` | coberto |
| Validação/erro — configuração ausente ou diferente de 2 falha | Mesmo teste, RED confirmado antes do atributo | Provar que o teste detecta remoção ou regressão do contrato | coberto |
| Acesso — N/A | Revisão de diff restrita a testes e documentação | Não existe usuário, tenant, capability ou rota neste contrato | N/A — infraestrutura do runner |
| Estado/exceções — ciclo de vida e descarte continuam intactos | `FoundationPostgresFixtureTests` e `FoundationApiFactoryLifecycleTests` | Preservar falha primária, descarte e Ryuk sem limpeza duplicada | coberto |
| Concorrência — amostra causal selecionada observa até dois PostgreSQL | Comando focado da verificação + `docker ps --filter label=org.testcontainers=true` | Confirmar o efeito na amostra sem ampliar o contrato universal | coberto manualmente com evidência na PR |
```

- [ ] **Etapa 2: Acrescentar o diagnóstico durável ao runbook**

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

- [ ] **Etapa 3: Validar documentação e tamanho**

Executar:

```bash
git diff --check
git diff --stat origin/main...HEAD
git diff -- docs/verification/issue-295-foundation-test-resource-budget-test-cases.md docs/runbooks/local-development.md
```

Esperado: sem whitespace inválido; documentação curta em português; nenhuma alteração fora do escopo.

- [ ] **Etapa 4: Commitar documentação**

Executar:

```bash
git add \
  docs/verification/issue-295-foundation-test-resource-budget-test-cases.md \
  docs/runbooks/local-development.md
git commit -m 'docs(test): record container resource budget'
```

### Tarefa 3: Reproduzir o efeito, executar o gate e revisar

**Arquivos:**
- Somente verificar: todos os arquivos alterados desde `origin/main`.

**Interfaces:**
- Consome: implementação e documentação das Tarefas 1–2.
- Produz: evidência focada, gate público completo e diff pronto para PR.

- [ ] **Etapa 1: Executar contratos de configuração, ciclo de vida e isolamento**

Executar:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TestAssemblyConfigurationTests|FullyQualifiedName~FoundationPostgresFixtureTests|FullyQualifiedName~FoundationApiFactoryLifecycleTests' \
  --logger 'console;verbosity=normal'
```

Esperado: 0 falhas e no máximo duas collections agendadas simultaneamente. A
quantidade de PostgreSQL observada neste comando não é garantia universal,
porque um teste pode criar múltiplos fixtures internamente.

- [ ] **Etapa 2: Executar a amostra causal da issue #295**

Executar:

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

Esperado: 10/10 verdes; nesta amostra específica, até dois
`postgres:17.9-alpine` mais um Ryuk na sessão; zero recursos da sessão após
encerrar. A observação da amostra não amplia o contrato para todo teste.

- [ ] **Etapa 3: Congelar escopo e executar o gate público completo**

Executar:

```bash
./scripts/verify-public.sh
```

Esperado: exit 0 em instalação, lint, format, build, scripts Foundation e todos os projetos públicos; não executar novamente salvo correção de bloqueio.

- [ ] **Etapa 4: Recalcular tamanho e conferir contratos**

Executar:

```bash
git diff --check origin/main...HEAD
git diff --stat origin/main...HEAD
git diff --numstat origin/main...HEAD
git status --short --branch
```

Esperado: 4 arquivos não-teste, 2 arquivos de teste/configuração, entre 380 e
500 linhas manuais, zero geradas, sem arquivo inesperado ou alteração não
commitada.

- [ ] **Etapa 5: Solicitar revisão independente e consolidar uma correção**

Revisar `origin/main...HEAD` contra a issue #295 e o desenho, priorizando:

- contrato real do atributo xUnit v2;
- teste RED/GREEN honesto;
- limite por processo e ausência de promessa global;
- ciclo de vida/Ryuk intactos;
- documentação sem instrução destrutiva ou automática.

Corrigir findings Critical/Important em uma única rodada, repetir somente os testes afetados e, se o gate completo já estava verde, executar validação proporcional sem ampliar escopo.

- [ ] **Etapa 6: Preparar push e PR**

Confirmar branch, commits, base `main`, issue #295, evidência focada, gate público e revisão. Usar a descrição da PR para registrar medições transitórias e `Closes #295`; manter a worktree para feedback.

### Tarefa 4: Definir seleção proporcional e gates estratégicos

**Arquivos:**
- Modificar: `AGENTS.md:3-12`
- Modificar: `docs/runbooks/public-verification.md`
- Modificar: `docs/verification/issue-295-foundation-test-resource-budget-test-cases.md`

**Interfaces:**
- Consome: política aprovada no desenho da issue #295 e comandos públicos já existentes.
- Produz: regras obrigatórias curtas em `AGENTS.md`, matriz operacional completa no runbook e BDD rastreável no manifesto.

- [ ] **Etapa 1: Substituir a regra genérica de testes em `AGENTS.md`**

Manter as regras compactas e obrigatórias: RED/GREEN mínimo; projeto e
consumidores diretamente afetados; CI como gate completo normal; gate local
completo no máximo uma vez somente para mudança compartilhada, banco/segurança,
mais de um projeto público, falha cruzada, CI indisponível ou pedido explícito;
documentação somente com validação pertinente; Docker pesado nunca sobreposto a
outra sessão VINX. Referenciar `docs/runbooks/public-verification.md` para a
matriz e os comandos.

- [ ] **Etapa 2: Reescrever o runbook público em português**

Preservar a descrição de `verify-public.sh`, dos verificadores EF e do job
Storage. Adicionar, nesta ordem:

1. fases `Investigação/RED`, `Implementação/GREEN`, `Correção` e `Fechamento`;
2. matriz para documentação, web/Vitest/Playwright, AI, Foundation Unit,
   Foundation Integration, Hosts, BDD, EF/migrations e Storage;
3. comandos exatos dos projetos/scripts já versionados;
4. gatilhos do gate local completo e regra de consumidores transitivos;
5. preflight de processos/sessões Docker, espera sem interrupção e confirmação
   de cleanup próprio;
6. distinção entre evidência focada, gate local completo e CI.

Documentar exatamente estes comandos de fechamento por área:

```bash
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm run test:no-direct-supabase
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.AI.IntegrationTests/Vinx.Erp.AI.IntegrationTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'Category!=SupabaseStorage'
dotnet test tests/public/Vinx.Erp.Foundation.Bdd/Vinx.Erp.Foundation.Bdd.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
./scripts/verify-supabase-storage.sh
./scripts/verify-public.sh
```

Para RED/GREEN, o runbook orienta acrescentar `--filter` com o nome completo do
método/classe realmente alterado, sem fornecer filtro genérico copiável.

- [ ] **Etapa 3: Estender o manifesto BDD**

Adicionar cinco linhas curtas para seleção de testes: mudança localizada;
impacto ambíguo; acesso N/A; gate completo após freeze sem repetição documental;
e concorrência entre worktrees. Mapear para `AGENTS.md`, o runbook e revisão
manual justificada; não criar teste que apenas procure texto-fonte.

- [ ] **Etapa 4: Validar caminhos, comandos e documentação**

Executar:

```bash
git diff --check
test -f AGENTS.md
test -f docs/runbooks/public-verification.md
test -x scripts/verify-public.sh
test -x scripts/verify-supabase-storage.sh
git ls-files 'tests/public/**/*.csproj' | sort
```

Esperado: exit 0; sete projetos públicos listados; nenhuma referência a script
ou projeto inexistente; nenhum workflow ou script alterado.

- [ ] **Etapa 5: Commitar a política**

```bash
git add AGENTS.md docs/runbooks/public-verification.md \
  docs/verification/issue-295-foundation-test-resource-budget-test-cases.md \
  docs/superpowers/plans/2026-08-24-issue-295-foundation-test-resource-budget.md
git commit -m 'docs(test): define strategic verification'
```

### Tarefa 5: Verificar, revisar e atualizar a PR

**Arquivos:**
- Somente verificar: todos os arquivos alterados desde `origin/main`.

**Interfaces:**
- Consome: política da Tarefa 4 e contrato xUnit já verificado.
- Produz: documentação revisada, branch remota e PR #297 atualizada.

- [ ] **Etapa 1: Executar verificação proporcional final**

Executar `git diff --check`, repetir
`TestAssemblyConfigurationTests` com `--no-build --no-restore`, confirmar árvore
limpa e ausência de processos/sessões Testcontainers próprios. Não repetir o
gate público completo porque a extensão não altera artefato executável.

- [ ] **Etapa 2: Recalcular escopo**

Esperado: 6 arquivos não-teste, 2 arquivos de teste/configuração, 720–790 linhas
manuais, 0 geradas e nenhum arquivo de CI/script alterado.

- [ ] **Etapa 3: Solicitar revisão independente**

Revisar fases, matriz, comandos, consumidores transitivos, gatilhos, ausência de
seleção automática, ownership Docker e coerência entre `AGENTS.md`, runbook,
desenho, manifesto, issue e PR. Corrigir todo finding Critical/Important antes
do push.

- [ ] **Etapa 4: Atualizar PR #297**

Push sem force, atualizar título/corpo com a política seletiva, manter
`Closes #295` e ler de volta base, head, estado, mergeabilidade e descrição.
