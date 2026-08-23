# Task 1 — Contrato de rotação atômica de identidade externa

## Resultado

Foi adicionada a migration `20260823150831_AddExternalIdentityReprovisioning` e
o contrato PostgreSQL
`foundation.reprovision_external_identity(user_id, expected_identity_id,
expected_issuer, expected_old_subject, new_subject, correlation_id)`.

A função `SECURITY DEFINER` pertence a `vinx_identity_owner`, usa
`search_path=pg_catalog, foundation, pg_temp` e aceita `EXECUTE` somente de
`vinx_migrator`. Ela bloqueia o usuário e a linha exata de identidade, mantém
os mesmos IDs de usuário e identidade externa, troca somente `subject`, avança
`sessions_valid_after`, revoga sessões ativas, preserva memberships/roles/
capabilities e a referência do operador de plataforma, e insere uma auditoria
global redigida na mesma transação. Os resultados estáveis são
`reprovisioned`, `already_configured`, `conflict` e `invalid`.

## Evidência RED → GREEN

### RED obrigatório

Antes de criar a migration, foi executado:

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~ExternalIdentityReprovisioningFunctionTests -v minimal
```

Resultado exato: `Failed: 1, Passed: 0, Skipped: 0, Total: 1` (8 s), com a
falha PostgreSQL esperada:

```text
Npgsql.PostgresException : 42883: function foundation.reprovision_external_identity(uuid, uuid, text, text, text, uuid) does not exist
POSITION: 20
```

Não houve falha de compilação, fixture ou setup antes do `undefined_function`.

### GREEN final

Após gerar a migration por EF e editar somente o SQL do contrato, o mesmo
comando focado terminou com `Failed: 0, Passed: 7, Skipped: 0, Total: 7`
(34 s). Os sete cenários usam PostgreSQL real e cobrem:

- rotação exata, barreira de sessão, revogação, auditoria, grafo de
  autorização, operador de plataforma e resolução da nova identidade;
- owner, `search_path`, `EXECUTE` de migrador e ausência de mutação por
  runtime/public;
- conflito por tupla antiga divergente ou novo subject já associado;
- rollback total quando `audit.append_event` não pode ser chamado;
- replay idempotente, corrida de dois novos subjects e rollback fail-closed
  depois de uma recuperação efetiva.

## Guardas e regressões afetadas

O guarda de modelo foi executado depois da geração:

```bash
FOUNDATION_MIGRATOR_CONNECTION='Host=127.0.0.1;Port=5432;Database=postgres;Username=vinx_migrator;Password=design-time-only' \
  dotnet ef migrations has-pending-model-changes \
  --project src/Foundation/Vinx.Erp.Foundation \
  --startup-project src/Hosts/Vinx.Erp.ApiHost
```

Resultado: `No changes have been made to the model since the last migration.`
O valor é local, não secreto e serve somente para a factory de design-time;
esse comando não conecta ao banco. A snapshot EF não mudou, corretamente,
porque a entrega adiciona apenas SQL de migration e nenhuma entidade/modelo.

Regressões executadas com PostgreSQL real:

| Seleção | Resultado |
| --- | --- |
| `ExternalIdentityReprovisioningFunctionTests` | 7/7, 34 s |
| dois métodos de downgrade em `SessionMigrationTests` | 4/4, 45 s |
| `InternalSessionAdmissionServiceTests` | 20/20, 1 min 50 s |
| `IdentityGateEndpointTests` | 16/16, 3 min 7 s |

Durante a primeira seleção de regressão, os downgrades legados retornaram
`42501` porque a nova migration tentava adquirir locks depois de trocar para
o owner de identidade com privilégio de coluna. A correção moveu esses locks
para o contexto `vinx_migrator` e concedeu/revogou somente
`user_sessions.revocation_reason` para o guarda próprio do `Down`. A seleção
de downgrade terminou em 4/4 depois da correção.

`git diff --check` terminou sem saída. O gate público completo não foi
executado, conforme instrução explícita da tarefa.

## Arquivos

- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260823150831_AddExternalIdentityReprovisioning.cs`
- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260823150831_AddExternalIdentityReprovisioning.Designer.cs` (gerado por EF)
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/ExternalIdentityReprovisioningFunctionTests.cs`
- `docs/superpowers/plans/2026-08-23-issue-241-identity-reprovisioning.md` (checklist de Task 1 concluído)
- `.superpowers/sdd/2026-08-23-issue-241-identity-reprovisioning/task-1-report.md`

`FoundationDbContextModelSnapshot.cs` foi reavaliada pelo EF, mas não teve
diferença material por não haver mudança de modelo.

## Contagem

- Linhas manuais de implementação: 760 (193 da migration + 567 dos testes).
- Linhas geradas: 1.677 no designer da migration.
- Mudança gerada na snapshot: 0 linhas.

As 760 linhas manuais permanecem dentro do alvo de 800 linhas para a fatia.

## Auto-revisão

- A função atualiza a linha de `external_identities` existente; não insere
  usuário, identidade, membership, role, capability ou operador.
- A checagem de replay acontece sob lock da mesma identidade antes da checagem
  da tupla antiga, evitando nova auditoria e nova transição de sessão.
- O lock do usuário serializa novos subjects concorrentes; o lock da identidade
  e o índice único `(issuer, subject)` protegem conflitos do novo subject.
- O audit é global, com metadados `{}`, sem issuer, subject, e-mail, correlação
  ou conteúdo de provider; erro de auditoria reverte todas as alterações.
- `Down` bloqueia o downgrade quando a auditoria ou a revogação própria da
  recuperação já existem e restaura os grants adicionais do owner.

## Preocupações e limites

- Esta entrega é exclusivamente o contrato de banco. Não há chamada a
  provider, configuração de PlatformBootstrap, API HTTP ou fluxo de Task 2.
- Não foram executados o gate público completo, push ou PR, por instrução.
- Uma revisão independente de PR continua necessária antes de merge, conforme
  as regras do repositório; esta entrega contém auto-revisão e evidência local,
  não substitui essa revisão.
