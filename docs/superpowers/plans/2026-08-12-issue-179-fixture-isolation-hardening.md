# Isolamento do fixture PostgreSQL — plano de implementação

**Objetivo:** complementar #174/#177 com identidade exclusiva por fixture,
cleanup que preserva a causa primária e isolamento em duas inicializações reais.

## Limites

- Issue #179; branch `codex/issue-174-fixture-isolation-hardening`; base #177.
- Não alterar produção, provider, imagem, pacote, paralelismo global, #173 ou frontend.
- Não adicionar retry, polling ou `Task.Delay`: não há causa transitória provada.
- Não registrar senha ou connection string completa.
- A fatia usa sete arquivos e deve permanecer abaixo de 800 linhas manuais
  quando rebaseada em `main` após a merge de #177.

## Desenho

- O fixture cria database e senha efêmeros antes do container; o endpoint
  confirma database, usuário, porta `5432`, família PostgreSQL e `Pooling=false`.
- Dois fixtures usam Testcontainers reais. A seam interna existe apenas para
  induzir falhas de start/dispose e de lifecycle na factory.
- `FixtureCleanup.CompleteAsync` tenta todos os cleanups: preserva a causa
  primária quando estiver sozinha e agrega primária primeiro quando houver
  falhas secundárias.
- A factory aplica a regra a bootstrap, migração e seed; tenta host, JWT e
  fixture nessa ordem. A API pública continua expondo fixture real.

## Execução concluída

- [x] RED/GREEN de endpoint PostgreSQL válido com database ou usuário esperado
  incorreto; erros são sanitizados.
- [x] RED/GREEN de database exclusivo e duas fixtures reais iniciadas e
  descartadas com `Task.WhenAll`.
- [x] RED/GREEN de falha primária + cleanup de fixture, incluindo nova tentativa
  de dispose após falha.
- [x] RED/GREEN de migração e seed na factory, preservando a primária e tentando
  host, JWT e fixture; cobertura direta de `DisposeAsync` com três falhas.
- [x] Manifesto BDD em `docs/verification/issue-179-fixture-isolation-hardening-test-cases.md`;
  acesso é N/A por ser infraestrutura de testes.
- [x] Repetição serial do cenário concorrente cinco vezes.
- [x] Formatação, `verify-public.sh` e `git diff --check`.

## Verificação focal

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~VerifyEndpointAsync|FullyQualifiedName~Concurrent_fixtures_use_distinct_verified_postgresql_endpoints|FullyQualifiedName~StartAsync_preserves_primary_failure_when_cleanup_also_fails|FullyQualifiedName~DisposeAsync_retries_after_a_failed_container_cleanup|FullyQualifiedName~PrepareAsync_preserves_primary_failure_when_all_cleanups_also_fail|FullyQualifiedName~CreateWithAuthenticatedIdentityAsync_preserves_seed_failure_when_all_cleanups_also_fail|FullyQualifiedName~DisposeAsync_attempts_all_cleanups_when_each_fails'
```
