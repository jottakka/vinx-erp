# Auditoria global Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Entregar a PR-base da issue #21: auditoria global tipada e atômica, necessária antes de sessões e desativação.

**Architecture:** AuditEvent recebe discriminador AuditScope. Eventos company continuam exigindo empresa; eventos global não carregam empresa. A função audit.append_event é substituída de forma versionada, preserva SECURITY DEFINER, trigger append-only e privilégios mínimos. Não há sessão, middleware, endpoint, Supabase ou mudança de provider nesta PR.

**Tech Stack:** .NET 10, EF Core/Npgsql, PostgreSQL, xUnit e verificadores públicos existentes.

## Global Constraints

- Basear esta PR em origin/main c0ee6b8, branch codex/issue-21-audit-global.
- Esta é PR 1A da stack #21. Escopo somente: AuditScope, contratos AuditEvent/AuditEventData, append_event, migration, grants, snapshot, testes e documentação Foundation.
- Não criar UserSession, gate, endpoint, claim Supabase, desativação, SECURITY DEFINER de identidade, MFA/AAL, recuperação ou rate limit.
- EF/Npgsql persiste o modelo. SQL fica somente na migration que atualiza a função de auditoria já existente.
- AuditScope.Global exige requestedCompanyId nulo; AuditScope.Company exige requestedCompanyId não nulo. Não usar Guid.Empty, nome de ação especial ou fallback implícito.
- Função audit.append_event permanece SECURITY DEFINER, com search_path fixo e EXECUTE somente para vinx_runtime.
- Atualizar todos os produtores atuais para AuditScope.Company, exceto company_context_required, que é AuditScope.Global.
- Não ler, listar ou executar quality/private. Documentação somente em português.
- Executar testes públicos focados durante desenvolvimento e ./scripts/verify-public.sh uma vez antes do push. Sol revisa a tarefa; Qwen 3.8 Max revisa a PR.
- Estimativa: 16 a 19 arquivos, 340 a 470 linhas manuais; migration, designer e snapshot não contam nas linhas.

## File Structure

| Arquivo | Responsabilidade |
|---|---|
| src/Foundation/Vinx.Erp.Foundation/Audit/AuditScope.cs | Enum Global/Company. |
| src/Foundation/Vinx.Erp.Foundation/Audit/AuditEvent.cs | Invariante persistida de escopo/empresa. |
| src/Foundation/Vinx.Erp.Foundation/Audit/AuditEventData.cs | Entrada tipada para append. |
| src/Foundation/Vinx.Erp.Foundation/Audit/AuditAppender.cs | Chamada à nova assinatura SQL. |
| src/Foundation/Vinx.Erp.Foundation/Authorization/*Service.cs | Produtores atuais passam escopo explícito. |
| src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/FoundationEntityConfigurations.cs | Mapeamento/constraint do modelo. |
| src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations | Schema, função, grants e snapshot EF. |
| tests/public/Vinx.Erp.Foundation.IntegrationTests | Contrato, privilégio e atomicidade. |
| tests/public/Vinx.Erp.Foundation.UnitTests | Invariantes puras de AuditScope. |
| docs/database/foundation-schema.snapshot.sql | Snapshot gerado da migration. |
| docs/modules/foundation.md | Regra durável de auditoria global. |

---

### Task 1: Fixar contrato tipado e testes verdes de auditoria global

**Files:**
- Create: src/Foundation/Vinx.Erp.Foundation/Audit/AuditScope.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Audit/AuditEvent.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Audit/AuditEventData.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Audit/AuditAppender.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Authorization/CapabilityMapService.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Authorization/RoleAssignmentService.cs
- Create or modify: tests/public/Vinx.Erp.Foundation.UnitTests/Audit/*Tests.cs
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/AuditPrivilegeTests.cs

**Interfaces:**
- Produces enum AuditScope { Global, Company }.
- AuditEvent and AuditEventData receive AuditScope before requestedCompanyId. AuditEventData deixa de ser record posicional sem corpo: expõe propriedades imutáveis e construtor validado.
- AuditAppender sends scope text to audit.append_event.
- Existing authorization services use AuditScope.Company; company_context_required uses AuditScope.Global.

- [ ] **Step 1: Write failing unit tests**

Adicionar testes de construtor que mostram os quatro pares e enum inválido:

~~~
Assert.Throws<ArgumentException>(() => new AuditEventData(
    actorId, AuditScope.Global, companyId, AuditSource.Web, "x", "x", null,
    AuditOutcome.Success, correlationId, null, "{}"));

Assert.Throws<ArgumentException>(() => new AuditEventData(
    actorId, AuditScope.Company, null, AuditSource.Web, "x", "x", null,
    AuditOutcome.Success, correlationId, null, "{}"));

Assert.Throws<ArgumentOutOfRangeException>(() => new AuditEventData(
    actorId, (AuditScope)999, null, AuditSource.Web, "x", "x", null,
    AuditOutcome.Success, correlationId, null, "{}"));
~~~

Cada teste deve nomear a mutação que ele captura: permitir `global/uuid`, permitir `company/null` ou persistir enum indefinido. Em `AuditPrivilegeTests`, atualizar as três chamadas existentes de `AuditEvent` para o construtor com scope e mover os asserts puros de construtor para a regra explícita `Global/Company`; conservar nesta Task somente essa compatibilidade de compilação, não os testes de banco/grants da Task 2. Não criar teste de texto de migration.

- [ ] **Step 2: Run RED**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~Audit"
~~~

Expected: FAIL porque AuditScope e o contrato tipado ainda não existem.

- [ ] **Step 3: Implement contracts and update producers**

Criar AuditScope. Em AuditEvent/AuditEventData, validar o enum por switch exaustivo e o par scope/empresa antes de guardar propriedades. Remover exceção especial baseada no texto company_context_required.

~~~
if (scope == AuditScope.Global && requestedCompanyId is not null)
    throw new ArgumentException("Global audit events cannot carry a company.");
if (scope == AuditScope.Company && requestedCompanyId is null)
    throw new ArgumentException("Company audit events require a company.");
~~~

O `default` do switch lança `ArgumentOutOfRangeException`; não converter enum inválido em texto nem deixar o banco decidir. Alterar AuditAppender para serializar somente `global` ou `company`. Alterar CapabilityMapService: company_context_required é global; todos outros eventos atuais são company. Alterar RoleAssignmentService para company. Não mudar ações, correlação ou metadata já existentes.

- [ ] **Step 4: Run focused GREEN contract tests**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter "FullyQualifiedName~Audit"
dotnet build Vinx.Erp.sln --no-restore
~~~

Expected: GREEN. Os testes de banco só serão adicionados e levados de RED a GREEN integralmente na Task 2; a Task 1 não termina vermelha.

### Task 2: Migrar função, grants e snapshot sem quebrar atomicidade

**Files:**
- Modify: src/Foundation/Vinx.Erp.Foundation/Persistence/Configurations/FoundationEntityConfigurations.cs
- Create: src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/*_AddAuditScope.cs
- Create: src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/*_AddAuditScope.Designer.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/FoundationDbContextModelSnapshot.cs
- Modify: docs/database/foundation-schema.snapshot.sql
- Modify: docs/modules/foundation.md
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/AuditTransactionTests.cs
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/AuditPrivilegeTests.cs
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/FoundationSchemaTests.cs
- Create: tests/public/Vinx.Erp.Foundation.IntegrationTests/AuditMigrationTests.cs

**Interfaces:**
- Produces audit.audit_events.scope text, values global/company.
- Produces audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb).
- A assinatura antiga de dez argumentos deixa de existir. A nova de onze argumentos recebe EXECUTE somente para vinx_runtime; nenhuma consulta usa `has_function_privilege` por nome para inferir a ausência da assinatura antiga.

- [ ] **Step 1: Write migration-level RED cases**

Expandir testes de integração para cobrir, em banco real:

~~~
Assert.Equal("global", await ReadAuditScopeAsync(eventId));
Assert.Null(await ReadRequestedCompanyAsync(eventId));
Assert.True(await HasExecuteAsync(
    "audit.append_event(uuid,text,uuid,text,text,text,uuid,text,uuid,text,jsonb)"));
Assert.False(await HasExecuteAsync(
    "audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb)"));
~~~

Usar `to_regprocedure` para provar que a assinatura antiga não existe e a nova existe uma única vez. Verificar owner `vinx_audit_owner`, `prosecdef = true`, `proconfig = ['search_path=audit, pg_catalog']`, ausência de EXECUTE para PUBLIC, EXECUTE somente para `vinx_runtime` na assinatura nova e ausência de INSERT/UPDATE/DELETE direto em `audit.audit_events`.

Chamar a função nova como runtime com os pares `global/null` e `company/uuid`; rejeitar `global/uuid`, `company/null`, scope `NULL` e scope desconhecido com SQLSTATE `23514` estável. Confirmar o trigger de append-only separadamente.

Adicionar duas provas transacionais. A primeira mantém a falha de produtor atual por `REVOKE EXECUTE`: ela demonstra fail-closed. Na segunda, a conexão administrativa revoga EXECUTE antes de a transação runtime começar. A transação runtime então persiste uma mutação permitida com `SaveChangesAsync`, chama o append sem privilégio e recebe `42501`; sai sem consulta/retry depois do erro PostgreSQL. Só após rollback/dispose, outra conexão administrativa confirma que nem a mutação nem o evento persistiram. Não tratar `23505`, `23514` ou `42501` com retry dentro da transação abortada; a corrida `23505` pertence à PR de sessões.

Em `AuditMigrationTests`, usando a conexão `vinx_migrator`, migrar até `RenameExternalIdentityProviderToIssuer`, inserir pela função de dez argumentos um evento legado com empresa e o evento legado `company_context_required/denied/null`. Então migrar até `AddAuditScope`. Confirmar que o primeiro recebeu backfill `company` e o segundo `global`, que os dois sobreviveram, que o default de `scope` foi removido e que a assinatura nova substituiu a antiga. A classificação é uma compatibilidade dos dados permitidos pelo contrato antigo: linhas legadas com empresa tornam-se `company`; sem empresa, `global`. O contrato novo não infere escopo pelo nome da ação.

Executar downgrade em dados somente-company e confirmar a função/constraint antigas. Inserir também um evento global, solicitar downgrade e confirmar falha fechada antes de remover/regravar dados globais.

- [ ] **Step 2: Run RED**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter "FullyQualifiedName~AuditTransactionTests|FullyQualifiedName~AuditPrivilegeTests|FullyQualifiedName~FoundationSchemaTests|FullyQualifiedName~AuditMigrationTests"
~~~

Expected: FAIL por scope ausente, função/grants antigos e migration ainda inexistente.

- [ ] **Step 3: Implement mapping and migration**

Mapear AuditScope para global/company e alterar primeiro entidade e mapping. Só então gerar a migration `AddAuditScope`:

~~~bash
dotnet ef migrations add AddAuditScope --project src/Foundation/Vinx.Erp.Foundation --startup-project src/Hosts/Vinx.Erp.ApiHost
~~~

Editar e revisar `Up` e `Down` manualmente após a geração. Como `vinx_audit_owner` não tem CREATE durável no schema `audit`, a conexão `vinx_migrator` primeiro concede temporariamente `USAGE, CREATE ON SCHEMA audit TO vinx_audit_owner`; então executa `SET ROLE vinx_audit_owner`. Todo DDL de `audit.audit_events`, constraints, funções, trigger, owner e grants fica sob esse role. No caminho normal, executar `RESET ROLE` e revogar CREATE do owner; ele conserva somente USAGE. Em falha, a transação de migration reverte a concessão temporária antes de expor estado parcial. O teste usa a conexão migrator para provar a migration e confirma no fim que o owner não retém CREATE no schema.

Em `Up`, adicionar coluna scope NOT NULL com default `company`; reclassificar cada evento legado sem empresa para `global` antes de instalar a nova constraint; conferir que os demais permanecem `company`; e remover o default ao final. Trocar constraint para:

~~~
(scope = 'global' AND requested_company_id IS NULL)
OR
(scope = 'company' AND requested_company_id IS NOT NULL)
~~~

Em `Up`, revogar EXECUTE de PUBLIC e vinx_runtime para a função antiga pela assinatura exata antes de removê-la. Criar a nova função com `p_scope`, revogar o EXECUTE padrão de PUBLIC e conceder somente a ela EXECUTE para vinx_runtime. A função valida valores e par scope/empresa, preserva SECURITY DEFINER e SET search_path TO audit, pg_catalog. Manter owner vinx_audit_owner e trigger de rejeição de UPDATE/DELETE.

Em `Down`, ainda sob `vinx_audit_owner` e antes de qualquer DDL destrutivo, recusar com `23514` se existir evento `global`; não apagar ou reescrever auditoria. Caso compatível, revogar grants e dropar a função nova, restaurar a assinatura antiga com owner `vinx_audit_owner`, SECURITY DEFINER, search_path e grants originais, restaurar a constraint antiga e só então remover a coluna `scope`. Testar os dois caminhos. Não adicionar limpeza automática sem nova aprovação.

- [ ] **Step 4: Generate artifacts and validate database**

Run:

~~~bash
./scripts/generate-foundation-schema-snapshot.sh --write
./scripts/verify-foundation-database.sh
~~~

Review migration and snapshot: scope não é anulável nem tem default final; check constraint não permite combinações inválidas; audit_events continua append-only; runtime não recebe privilégios diretos de mutação. Confirmar também que `Down` falha antes de alterar dados globais.

- [ ] **Step 5: Document, run GREEN, commit and review**

Atualizar docs/modules/foundation.md com global/company, regra de empresa e atomicidade do append. Não documentar detalhes de session ainda.

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter "FullyQualifiedName~AuditTransactionTests|FullyQualifiedName~AuditPrivilegeTests|FullyQualifiedName~FoundationSchemaTests"
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter "FullyQualifiedName~AuditMigrationTests"
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj
git add src/Foundation/Vinx.Erp.Foundation/Audit src/Foundation/Vinx.Erp.Foundation/Authorization src/Foundation/Vinx.Erp.Foundation/Persistence tests/public/Vinx.Erp.Foundation.IntegrationTests tests/public/Vinx.Erp.Foundation.UnitTests docs/database/foundation-schema.snapshot.sql docs/modules/foundation.md
git commit -m "feat: tipar auditoria global"
./scripts/verify-public.sh
~~~

Depois do gate, revisar com Sol e Qwen 3.8 Max. Corrigir achados críticos/importantes com re-revisão antes do push e draft PR.

## Execution Order

Terra implementa Task 1, Sol revisa. Terra implementa Task 2, Sol revisa. Qwen 3.8 Max revisa a branch completa. Esta PR é a base de codex/issue-21-sessions e codex/issue-21-user-deactivation; não criar essas branches até o commit base e a revisão final estarem limpos.
