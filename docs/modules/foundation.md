# Foundation module contract

Foundation owns the cross-cutting contracts that every ERP module consumes. It does not own Paguai, Service Desk, Kanban, HR, Notifications or AI business rules.

## Ownership

- Groups, companies, units, teams, users, employees and active memberships.
- Roles, named capabilities and scope grants: `self`, `team`, `unit`, `company` and `group`.
- Configurable approval policies and alçadas. A sole owner approver is data configuration, never a hard-coded module rule.
- Identity and file-storage provider boundaries: `IdentityProvider` and `FileStorage`.
- Durable audit, correlation context, redaction, OpenTelemetry conventions, health checks, idempotency keys and outbox technical contracts.

## Company scope

All commands carry one company. A person can hold different memberships, roles
and scopes in more than one company. The active context is one company or a
read-only all-authorized view. Every command, including an MCP command,
provides one explicit `empresa_id`; the server validates membership, capability
and scope regardless of frontend visibility.

The frontend receives effective capabilities only to shape menus, routes,
buttons, columns and data. It is never the authorization authority. API, MCP
and database protections deny forged or cross-company requests.

## Catálogo de capabilities

`foundation.capabilities` é catálogo técnico administrado exclusivamente por
migrations. Os nomes canônicos são identidade estável; o runtime apenas lê o
catálogo e não recebe `INSERT`, `UPDATE` ou `DELETE`. A migration preserva uma
linha legada que já use o mesmo nome, falha se o UUID canônico estiver ocupado
por outro nome e não permite downgrade que remova uma capability referenciada
por um grant. Para comandos atuais, somente grant de escopo `company` satisfaz
o requisito; os demais escopos falham fechados até existir regra específica.

## Identidade autenticada

A API valida o JWT do Supabase Auth e resolve o ator interno exclusivamente
pelo par imutável `issuer` + `subject` em `foundation.external_identities`.
E-mail não participa da resolução e uma identidade externa desconhecida recebe
`401`; a API não cria `User` nem `ExternalIdentity` durante uma requisição.
O runtime possui somente `SELECT` nessa tabela para essa consulta.
Antes dessa resolução, `session_id` deve ser UUID e `amr` deve conter
autenticação não `token_refresh`; ausência ou malformação falha fechada com
`401`. A prova não persiste token nem dado pessoal.

`InternalSessionAdmissionService` é usado pelo gate HTTP: bloqueia o usuário
por função SQL, grava uma sessão EF só quando a autenticação é estritamente
posterior à barreira e registra auditoria global na mesma transação. Repetição
ativa não cria auditoria; barreira, revogação e colisão produzem negação
auditada sem identificador externo. O gate cria o `ActorContext` e decide
entre `401` e `503` antes do endpoint protegido.

`GET /foundation/memberships` usa somente esse `ActorContext`. Em uma única
transação de leitura, descobre as memberships ativas do ator e projeta as
capabilities efetivas por empresa, em ordem de nome da empresa. Cada empresa
recebe contexto RLS próprio antes da leitura de grants; uma membership válida
sem grant retorna `capabilities: []`. A resposta não expõe papéis, identidade
externa ou dados de autenticação. Falha de descoberta retorna `503` seguro.
`GET /foundation/capabilities?companyId=` continua sendo o contrato de uma
única empresa e preserva suas negações auditáveis.

`POST /foundation/identity/sessions/revoke-all` não recebe corpo, usuário,
empresa nem alvo. Ele usa somente o `ActorContext` estabelecido pelo gate e
revoga todas as sessões da própria pessoa pela função transacional existente.
`Revoked` e `NoChange` retornam `204`; indisponibilidade retorna
`503 application/json` com objeto de uma única propriedade `code` e valor
`identity_gate_unavailable`. A operação mantém o mesmo estado final em
repetições concorrentes: `204/204` é permitido quando ambas as requisições
passam pelo gate antes da barreira, e `204/401` quando uma observa a barreira
antes do endpoint. O teste determinístico após o primeiro `204` espera `401`.
Listagem e revogação individual (#79), interfaces de sessão (#80), administração de sessão (#81), MFA/AAL (#18),
recuperação (#19) e logout/banimento no provedor (#20) continuam adiados.
Configure a API com `SUPABASE_AUTH_ISSUER` e `SUPABASE_AUTH_AUDIENCE`;
`FOUNDATION_RUNTIME_CONNECTION`, `SUPABASE_STORAGE_URL` e
`SUPABASE_STORAGE_SERVICE_ROLE_KEY` continuam obrigatórias para a infraestrutura.

## Contract version

`1.0`

## Modelo de entidades

Entidades mutáveis compartilham identidade, auditoria, exclusão lógica e versão de concorrência por classes-base do domínio, sem dependência do EF. O mapeamento permanece em `Persistence`, e somente entidades que pertencem a uma empresa implementam `ICompanyScoped`.

## Data and audit rules

Business and configuration data use soft delete with `deleted_at`, `deleted_by_user_id` and `deleted_reason`; a restore is authorized and audited. Audit events are append-only and use retention rather than soft delete. Module data remains explicitly company-scoped; an authorized group-wide view is a union, never a tenant-less record.

Cada evento de auditoria possui `scope`: `company` exige uma
`requested_company_id`, enquanto `global` exige que ela seja nula. O runtime
insere eventos somente por `audit.append_event`; não recebe permissões diretas
de alteração na tabela. O append de sucesso participa da mesma transação da
mutação de negócio: se ele falhar, a mutação também é revertida. A migração não
permite downgrade enquanto houver eventos globais, para não reescrever ou
apagar auditoria histórica.

Idempotent writes replay a matching request and payload. Reusing a request with a
different payload returns `idempotency_conflict`; a separate write that loses a
business-database integrity race returns the redacted `integrity_conflict` and
records a denied audit event. Neither response exposes database diagnostics.

## Observabilidade e logging

Todos os hosts configuram `AddVinxLogging`, que preserva `ILogger<T>` nativo e
emite somente console JSON com timestamp UTC ISO 8601. O filtro reduz os logs
rotineiros de `Microsoft.AspNetCore` para `Warning`, enquanto `TraceId` e
`SpanId` acompanham o escopo quando houver uma `Activity`; tags e baggage não
entram no logging.

Hosts HTTP usam `UseVinxRequestCorrelation` antes dos endpoints. O middleware
gera um `CorrelationId` UUID no servidor, registra-o no contexto da requisição,
marca a `Activity` atual e o propaga por scope. Ele não aceita correlação do
cliente, não registra a requisição e sempre descarta o scope, inclusive em
exceções. Logs, métricas e auditoria nunca incluem JWT, refresh token,
credencial, segredo, PII, conteúdo de arquivo, prompt ou payload bruto.

## Deletion strategy

See Data and audit rules above; this section introduces no second deletion policy.

## Migration notes

The first migration is `InitialFoundationBaseline`.

A fronteira atual de ownership usa `vinx_migrator LOGIN NOINHERIT
NOBYPASSRLS`, `vinx_runtime LOGIN NOINHERIT NOBYPASSRLS`,
`vinx_audit_owner NOLOGIN NOINHERIT NOBYPASSRLS`,
`vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS` e
`vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS`. O owner de
identidade possui somente `USAGE` em `foundation`, `SELECT` em
`foundation.users` e `UPDATE` apenas na coluna `sessions_valid_after`,
necessário para o `FOR UPDATE` da função de admissão de sessão. O owner de
autorização recebe leitura somente de `memberships` e `companies` para
`current_actor_memberships()`, e suas policies RLS exclusivas não se aplicam
ao runtime. O runtime não recebe esses `UPDATE`s nem leitura global dessas
tabelas. Essas roles são distintas das roles legadas do Supabase: `anon`,
`authenticated` e `service_role`.

## Module responsibilities

Each module defines its own use cases, state transitions and resource-level rules. It asks Foundation to evaluate the relevant named capability and scope, resolve configured alçadas, record an audit event, propagate correlation and use provider boundaries. It must not add a broad shared abstraction merely for future reuse.
