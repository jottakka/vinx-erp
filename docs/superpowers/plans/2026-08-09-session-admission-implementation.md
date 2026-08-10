# Admissão interna de sessões — plano de implementação

> **Para agentes:** execute as tarefas em ordem, com TDD e revisão independente
> após cada tarefa. Esta é uma fatia de #21; não encerra a issue.

## Objetivo e limite

Persistir e admitir de forma atômica uma sessão cuja prova Supabase já foi
validada. A fatia entrega estado EF, lock no banco, auditoria e logs
operacionais. Não adiciona middleware, endpoints, `revoke-all`, desativação,
MFA nem troca de provider.

O gate HTTP e a revogação global própria formam a próxima PR, sobre esta.

## Contrato

Criar `InternalSessionAdmissionService` com:

```csharp
Task<SessionAdmissionResult> AdmitAsync(
    Guid userId,
    ExternalIdentityProof proof,
    AuditSource source,
    Guid correlationId,
    CancellationToken cancellationToken);
```

O resultado tem quatro estados: `Admitted`, `Active`, `Denied` e
`Unavailable`. As negações estáveis são `authentication_not_after_barrier`,
`session_revoked` e `session_identity_mismatch`. `source` é informado pelo
host confiável, preservando Web, MCP e Worker sem acoplamento. Cancelamento é
propagado.

Não persistir JWT, refresh token, e-mail, IP, User-Agent, conteúdo de `amr`,
nem `provider_session_id` em logs ou auditoria.

## Modelo e concorrência

- `User` recebe `SessionsValidAfter` UTC anulável e `RowVersion` de
  concorrência.
- `UserSession` é global em `foundation.user_sessions`: `Id`, `UserId`,
  `Issuer`, `ProviderSessionId`, `ProviderAuthenticatedAt`, `FirstSeenAt`,
  `TokenExpiresAt`, `RevokedAt`, `RevokedByUserId`, `RevocationReason` e
  `RowVersion`.
- Há unicidade em `(issuer, provider_session_id)`, índice em
  `(user_id, revoked_at)` e FKs restritivas.
- A migration cria
  `foundation.lock_user_for_session_admission(uuid)`, `SECURITY DEFINER`,
  `search_path` fixo e `SELECT ... FOR UPDATE`. Ela retorna a barreira do
  usuário. O runtime recebe apenas `EXECUTE` nessa função; não recebe
  `SELECT` em `foundation.users`.
- A admissão abre transação, adquire esse lock e só admite quando
  `ProviderAuthenticatedAt` é estritamente posterior à barreira. Igualdade é
  negada.
- A primeira PR concede somente `SELECT` e `INSERT` em `user_sessions`; não
  concede `UPDATE` nem `DELETE`. A revogação posterior acrescentará a
  permissão estritamente necessária.
- O `Down` adquire `LOCK TABLE foundation.user_sessions IN ACCESS EXCLUSIVE MODE`
  e `LOCK TABLE foundation.users IN ACCESS EXCLUSIVE MODE` antes de verificar
  e remover. Ele falha fechado se houver sessão ou barreira, sem apagar estado
  numa corrida.

## Auditoria e observabilidade

- Nova sessão: `identity_session_admitted`, `global`, `success`, recurso
  `user_session` e metadata `{}`.
- Barreira, sessão revogada ou colisão: `identity_session_admission_denied`,
  `global`, `denied`, com razão estável; em colisão, não revelar o recurso de
  outro usuário.
- Toda negação de política usa a origem recebida e ocorre na mesma transação
  da auditoria. Falha de auditoria retorna `Unavailable`; mutação e auditoria
  parcial sofrem rollback. Falha técnica não tenta segunda auditoria: ela só é
  registrada em log operacional e retorna `Unavailable`.
- `ILogger<InternalSessionAdmissionService>` usa templates constantes e as
  propriedades `Operation`, `Module`, `Result` e `Reason`. Nova admissão é
  `Information`, negações esperadas `Debug`, colisão entre usuários `Warning`
  e perda de banco/auditoria `Error`. Não registrar identificadores, claims ou
  PII. `CorrelationId` é aberto apenas no scope herdado, nunca no template.

## Arquivos previstos

| Área | Arquivos | Linhas manuais |
|---|---:|---:|
| Domínio, EF, migration e grants | 7–8 | 250–300 |
| Serviço, auditoria e logging | 2–3 | 140–180 |
| Testes públicos | 4–5 | 180–230 |
| Documentação e snapshot | 4 | 45–65 |
| **Total** | **18–20** | **630–790** |

Migration, designer e snapshot são gerados; contam como arquivos, não como
linhas manuais.

## Tarefa 1 — estado EF e banco

**Criar**

- `src/Foundation/Vinx.Erp.Foundation/Domain/UserSession.cs`
- `tests/public/Vinx.Erp.Foundation.UnitTests/UserSessionTests.cs`
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/SessionMigrationTests.cs`

**Modificar**

- `src/Foundation/Vinx.Erp.Foundation/Domain/User.cs`
- `src/Foundation/Vinx.Erp.Foundation/Persistence/FoundationDbContext.cs`
- `src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/FoundationEntityConfigurations.cs`
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/FoundationSchemaTests.cs`
- `docs/database/foundation-schema.snapshot.sql` (gerado)
- migration, designer e `FoundationDbContextModelSnapshot` (gerados)

**Testes primeiro**

1. O domínio rejeita `ProviderAuthenticatedAt >= TokenExpiresAt`, revogação
   parcial e motivo vazio; permite uma sessão ativa válida.
2. A migration cria tabela, checks, índices, FKs, função segura e a matriz de
   grants exata: `SELECT`/`INSERT` em `user_sessions`, execução apenas da
   função nova, nenhum privilégio direto em `users`.
3. O downgrade bloqueia concorrente de inserção/avanço de barreira antes da
   verificação, falha quando existir sessão ou `sessions_valid_after` e não
   apaga nem reescreve dados.

**Implementação**

Adicionar o modelo e a configuração EF. Gerar a migration após os testes RED;
ela deve usar `clock_timestamp()` somente no banco, revogar `PUBLIC`, fixar
`search_path TO foundation, pg_catalog`, definir dono mínimo da função e
revogar qualquer privilégio que não pertença ao runtime. Gerar e conferir o
snapshot.

## Tarefa 2 — serviço de admissão, auditoria e logs

**Criar**

- `src/Foundation/Vinx.Erp.Foundation/Identity/SessionAdmissionResult.cs`
- `src/Foundation/Vinx.Erp.Foundation/Identity/InternalSessionAdmissionService.cs`
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/InternalSessionAdmissionServiceTests.cs`
- `docs/verification/session-admission-test-cases.md`

**Modificar**

- `docs/superpowers/specs/2026-08-09-identity-sessions-deactivation-design.md`
- `docs/modules/foundation.md`

**Testes primeiro**

1. Uma prova nova cria uma linha e uma auditoria global; nova chamada retorna
   `Active`, sem repetir auditoria.
2. Duas admissões concorrentes criam uma única sessão e somente uma auditoria
   de sucesso.
3. Barreiras em `-1`, `0` e `+1` segundo produzem, respectivamente,
   `Denied`, `Denied` e `Admitted`.
4. Uma sessão revogada é negada; a mesma chave ligada a outro usuário é negada
   sem expor o usuário nem o identificador externo.
5. Toda negação de política é auditada com a `AuditSource` recebida; falha ao
   auditar sucesso ou negação retorna `Unavailable` e faz rollback, sem sessão
   nem auditoria parcial. Falha técnica só produz log `Error` com exceção.
6. Capturar logs e afirmar nível, as quatro propriedades do evento e o
   `CorrelationId` somente no scope; no evento `Error`, preservar a exceção e
   confirmar ausência de IDs, claims e PII.

**Implementação**

Registrar o serviço no host ainda não faz parte desta PR: a prova só será
consumida pelo middleware da próxima fatia. O serviço usa uma transação, chama
a função de lock e busca a sessão pelo índice único. Ao perder a corrida de
unicidade, faz rollback, limpa o `ChangeTracker`, relê sem rastrear e retorna
`Active` somente se a sessão pertence ao mesmo usuário e está ativa; caso
contrário registra negação segura. `OperationCanceledException` não é
convertida em indisponibilidade.

Atualizar a decisão durável com o evento
`identity_session_admission_denied` e declarar esta entrega interna; manter
documentação em português, compacta e sem detalhes sensíveis.

Criar `docs/verification/session-admission-test-cases.md` com a matriz BDD:

| Classe | Cobertura pública desta PR |
|---|---|
| Sucesso | Sessão nova, repetição ativa e auditoria única. |
| Validação e erros | Invariantes do domínio, barreira estrita e prova incompatível. |
| Acesso | Grants exatos do runtime e função de lock; HTTP é N/A nesta microfatia, pois o gate permanece na próxima PR de #21. |
| Estado e exceções | Sessão revogada, auditoria indisponível, rollback e log técnico. |
| Concorrência | Admissão dupla, colisão entre usuários e downgrade contra mutação. |

## Verificação

Durante as tarefas:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests --no-restore --filter 'FullyQualifiedName~UserSession'
DOCKER_CONTEXT=desktop-linux dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --no-restore --filter 'FullyQualifiedName~InternalSessionAdmissionServiceTests|FullyQualifiedName~SessionMigrationTests|FullyQualifiedName~FoundationSchemaTests'
```

Antes do push, executar uma única vez:

```bash
./scripts/generate-foundation-schema-snapshot.sh --write
./scripts/verify-foundation-database.sh
dotnet build Vinx.Erp.sln --no-restore
./scripts/verify-public.sh
```

Fazer revisão Sol independente e revisão Qwen 3.8 Max da PR completa antes
de publicar. Não ler, listar ou executar `quality/private`.
