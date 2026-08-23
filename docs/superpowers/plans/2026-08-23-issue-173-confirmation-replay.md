# Plano de implementação — replay de confirmação (#173)

> **Para agentes:** subskill obrigatória: use `superpowers:subagent-driven-development` (recomendado) ou `superpowers:executing-plans` por tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** Fazer confirmações concorrentes com o mesmo `RequestId` e payload convergirem no resultado `confirmed` já persistido, sem inspeção, scan, auditoria ou idempotência duplicados.

**Arquitetura:** Manter o advisory lock e o helper de reconciliação. Um interceptor determinístico de comando EF Core pausa A após seu read vazio de idempotência, deixa B idêntico confirmar e prova que A reconcilia o arquivo terminal por `ReturnConcurrentConfirmationReplayAsync`, sem `integrity_conflict` prematuro.

**Stack:** .NET 10, ASP.NET Core, interceptors EF Core, PostgreSQL/Testcontainers, xUnit.

## Restrições globais

- Partir de `origin/main` atual; não reutilizar nem cherry-pickar a branch obsoleta.
- Usar TDD RED → GREEN estrito e guardar os comandos falho/aprovado no relatório.
- Preservar membership, capability, tenant/RLS, malware, advisory lock, auditoria e idempotência.
- Mesmo pedido e payload retorna `200 confirmed`; payload divergente mantém `409 idempotency_conflict`; IDs distintos mantêm `409 integrity_conflict`.
- Não adicionar migration, pacote, polling, delay, retry, isolamento mais forte ou abstração genérica.
- Documentar em português compacto e não acessar `quality/private`.
- Contar cada linha manual de teste; linhas geradas são zero. Congelar escopo após a correção de revisão.

---

### Tarefa 1: Regressão determinística de read-skew e reconciliação mínima

**Arquivos:**
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs`
- Modificar: `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs`
- Criar: `docs/verification/issue-173-concurrent-confirmation-replay-test-cases.md`

**Interfaces:**
- Consome: `DocumentUploadService.ConfirmAsync`, `ReturnConcurrentConfirmationReplayAsync`, fixture PostgreSQL real existente, `ControlledFileStorage` e `ControlledMalwareScanner`.
- Produz: wiring de teste `FirstConfirmationIdempotencyReadGate` e contratos HTTP públicos inalterados.

- [x] **Passo 1: Adicionar gate determinístico e teste de integração falho**

  Adicionar hook opcional `DbCommandInterceptor` a `FoundationApiFactory` pelo padrão existente. Em `DocumentUploadEndpointTests`, implementar `FirstConfirmationIdempotencyReadGate` para que a primeira chamada `ReaderExecutedAsync` correspondente:

  1. encontre comando com `idempotency_records` e parâmetro `foundation.files.upload.confirm`;
  2. sinalize `FirstRead` só após a consulta vazia real;
  3. aguarde `Release` com cancellation token limitado;
  4. sempre permita release no cleanup do teste.

  Adicionar `Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit`: iniciar A, aguardar `FirstRead`, concluir B idêntico como `200 confirmed`, liberar A e verificar mesmo arquivo/`confirmed` nos dois corpos; uma inspeção, scan, auditoria de sucesso e linha de idempotência; nenhuma auditoria negada `integrity_conflict`.

- [x] **Passo 2: Executar o novo teste e registrar o RED esperado**

  Executar:

  ```bash
  dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit -v minimal
  ```

  Esperado: B retorna `200 confirmed`, A retorna `409 integrity_conflict` e falha a asserção de dois replays aprovados. Timeout, erro de setup ou exceção sintética não valem como RED.

- [x] **Passo 3: Implementar a correção mínima de produção**

  Em `PrepareConfirmationAsync`, trocar somente a negação direta de arquivo terminal pelo caminho de reconciliação existente:

  ```csharp
  if (!string.Equals(processingState, FileRecord.QuarantinedState, StringComparison.Ordinal))
  {
      return ConfirmationPreparation.Completed(
          await ReturnConcurrentConfirmationReplayAsync(
              actor,
              command,
              payloadHash,
              cancellationToken));
  }
  ```

  Não alterar tratamento de exceção da finalização, lock, isolamento transacional ou classificação de erro PostgreSQL.

- [x] **Passo 4: Executar o teste determinístico e registrar GREEN**

  Reexecutar o comando exato do Passo 2. Esperado: `1/1` aprovado, saída limpa e efeitos duráveis únicos.

- [x] **Passo 5: Adicionar regressão realista de payload divergente**

  Adicionar `Confirmation_with_same_request_id_and_different_file_returns_idempotency_conflict`: confirmar arquivo 1 e depois arquivo 2 na mesma empresa com o mesmo `RequestId`; verificar `409 idempotency_conflict`, arquivo 2 em quarentena e efeitos únicos originais sem duplicação.

- [x] **Passo 6: Executar regressão focada**

  Executar os dois testes novos e estes métodos existentes com uma expressão de filtro:

  - `Concurrent_confirmation_with_the_same_request_creates_one_confirmed_result`
  - `Concurrent_confirmations_with_distinct_request_ids_inspect_once_and_replay_their_results`
  - `Repeated_confirmation_replays_confirmed_state_without_a_second_audit_or_exposure`
  - `Confirmation_reauthorizes_after_private_inspection_finishes`

  Esperado: todos os selecionados aprovados, sem warnings ou errors.

- [x] **Passo 7: Adicionar manifest de verificação**

  Criar tabela compacta em português que mapeie classes BDD de sucesso, validação/erro, acesso, estado/exceção e concorrência aos métodos exatos. Marcar testes de autorização existentes e inalterados pelo nome; não copiar logs.

- [x] **Passo 8: Verificar escopo e fazer commit**

  Executar `git diff --check`, recalcular arquivos/linhas manuais e confirmar zero mudanças de migration/snapshot/lockfile. Commitar a fatia com assunto convencional curto. O relatório deve incluir RED, GREEN, saída focada, arquivos alterados e estimativa de tamanho. Não executar o gate público completo nesta tarefa; o controller o executa após revisão independente e correções.
