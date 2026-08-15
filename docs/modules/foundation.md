# Módulo Foundation

Foundation fornece os contratos transversais do ERP: identidade, empresas,
membership, capabilities, RLS, auditoria, idempotência e as aprovações
duráveis consumidas pelos módulos. Não contém regras de negócio de Paguai,
Service Desk, Kanban, HR, Notificações ou AI.

## Ownership

- grupos, empresas, unidades, times, usuários e memberships ativas;
- employees e fronteiras dos provedores de identidade e armazenamento:
  `IdentityProvider` e `FileStorage`;
- roles, capabilities nomeadas e escopos `self`, `team`, `unit`, `company` e
  `group`;
- políticas de aprovação configuráveis e alçadas; aprovador único é
  configuração de dados, nunca regra fixa do módulo;
- `foundation.approvals`, seus serviços de preparação, confirmação e veto;
- identidade, auditoria, correlação, redaction, telemetria, health checks,
  idempotência e contratos técnicos de outbox.

## Company scope

Todo comando recebe uma `empresa_id` explícita e o servidor resolve o ator por
contexto autenticado. A UI apenas molda menus; API, MCP e PostgreSQL negam
contexto forjado ou cruzado. Em aprovações, RLS é forçado e combina empresa e
ator; contexto ausente falha fechado.

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

O resolver interno de contexto parte somente do `ActorContext` e materializa
um snapshot sem filtro de memberships ativas, nome legível e capabilities por
empresa. Um `CapabilityRequirement` de escopo `company` deriva desse snapshot
o conjunto exato de empresas autorizado para leitura; a aplicação RLS usa
esse conjunto apenas durante a transação atual. O conjunto multiempresa não
participa de `INSERT`, `UPDATE`, `DELETE` nem de `WITH CHECK`: comandos
continuam usando `app.company_id`, e revogação só aparece no próximo snapshot.
Falha de identidade, setup ou banco não produz conjunto parcial; a operação
falha fechada e registra somente contexto operacional seguro.

`RevokeMembershipService` é um serviço interno de aplicação, registrado no
host sem rota HTTP, MCP ou interface. O comando traz empresa, membership e
`request_id`; exige membership ativa do ator e a capability
`foundation.memberships.revoke` exatamente em escopo `company`. A autorização
é sempre avaliada antes da idempotência. Depois dela, um replay com o mesmo
payload vence a busca da membership já excluída e devolve o sucesso original;
payload diferente retorna `idempotency_conflict` e novo `request_id` para a
membership revogada retorna `resource_not_found`. O alvo de outra empresa não
é revelado. A transição faz soft-delete, idempotência e auditoria
`foundation.memberships.revoke` na mesma transação; falha de auditoria desfaz
tudo. A operação registra a métrica segura `membership_revocation` quando a
auditoria ou a transação ficam indisponíveis. Ela não revoga sessão nem altera
as memberships de outras empresas.

`POST /foundation/identity/sessions/revoke-all` não recebe corpo, usuário,
empresa nem alvo. Ele usa somente o `ActorContext` estabelecido pelo gate e
revoga todas as sessões da própria pessoa pela função transacional existente.
`Revoked` e `NoChange` retornam `204`; indisponibilidade retorna
`503 application/json` com objeto de uma única propriedade `code` e valor
`identity_gate_unavailable`. Listagem e revogação individual (#79), interfaces
de sessão (#80), administração de sessão (#81), MFA/AAL (#18), recuperação
(#19) e logout/banimento no provedor (#20) continuam adiados.

## Aprovações duráveis

`PrepareApprovalService` exige membership ativa e
`ai.writes.confirm` exatamente em escopo `company`. Depois do parsing
estrutural, grava proposta, idempotência e auditoria na mesma transação. Replay
do mesmo `RequestId` e payload devolve a proposta sem auditoria duplicada;
payload diferente retorna `idempotency_conflict`; uma segunda proposta viva do
mesmo ator/empresa retorna `proposal_conflict` auditado.

`ConfirmApprovalService` rederiva o hash canônico (chaves ordenadas
recursivamente, números pela representação JSON, SHA-256 hexadecimal minúsculo
de 64 caracteres) e exige a mesma policy version. O claim é um `UPDATE`
condicional único com `now()` do PostgreSQL, `consumed_at IS NULL`,
`deleted_at IS NULL` e `expires_at > now()`. O resultado é `Claimed`; o mesmo
`RequestId` recebe `Replay`; divergência, perda de claim ou novo pedido após
consumo recebem `Conflict`; linha invisível, expirada ou vetada recebe
`ProposalUnavailable` conforme a reconciliação.

Cada confirmação, inclusive replay, reavalia membership/capability e RLS.
Veto também reavalia autorização em cada chamada e faz soft-delete terminal
com `deleted_reason = 'approval_vetoed'`, `request_id` do veto e auditoria.
Replay de veto é `AlreadyVetoed`; veto após consumo é conflito. Falha de
auditoria faz rollback; a mutação de negócio e sua evidência não se separam.

Os `RequestId` de preparação e confirmação são distintos. Um futuro serviço de
execução deverá ter idempotência própria, mas ainda não existe costura com o
harness. Foundation não promete execução exatamente-uma-vez.

## Invariantes persistentes

A migration declara, com nomes estáveis:

- constraints `ck_approvals_expires_after_creation`,
  `ck_approvals_payload_hash_format`, `ck_approvals_veto_complete`,
  `ck_approvals_consumption_complete` e `ck_approvals_terminal_exclusive`;
- índice único parcial `ix_approvals_company_id_actor_user_id`, filtrado por
  `consumed_at IS NULL AND deleted_at IS NULL`, garantindo uma proposta viva
  por ator/empresa;
- índices `ix_approvals_actor_user_id`,
  `ix_approvals_confirmation_request_id` (único parcial, somente quando o
  valor não é nulo) e
  `ix_approvals_proposal_id` (único);
- índice de idempotência
  `ix_idempotency_records_company_id_actor_user_id_command_name_r`.

`ApprovalUniqueViolationClassifier` só classifica os dois nomes pinados
(`ix_approvals_company_id_actor_user_id` como `LiveProposal` e
`ix_idempotency_records_company_id_actor_user_id_command_name_r` como
`Idempotency`); qualquer outro constraint é `Other` e não vira um conflito
inventado.

A preparação lê o relógio da transação, expira propostas vencidas antes de
testar conflito vivo e registra `approval_expired`; a janela de sweep usa o
instante inicial da transação. Expiração, veto e consumo são terminais e
mutuamente exclusivos. A Foundation mantém a linha terminal para replay e
auditoria; a purga física continua fora desta fatia, na issue #201.

## Contract version

`1.1`

## Modelo de entidades e dados

Entidades mutáveis compartilham identidade, auditoria, exclusão lógica e
versão de concorrência por classes-base do domínio, sem dependência do EF. O
mapeamento permanece em `Persistence`, e somente entidades que pertencem a uma
empresa implementam `ICompanyScoped`.

Dados de negócio e configuração usam soft-delete com `deleted_at`,
`deleted_by_user_id` e `deleted_reason`; restauração exige autorização e
auditoria. Eventos de auditoria são append-only e usam retenção, não
soft-delete. Dados de módulo permanecem explicitamente escopados por empresa;
uma visão autorizada de grupo é união de empresas, nunca registro sem tenant.

Cada evento de auditoria possui `scope`: `company` exige
`requested_company_id`, enquanto `global` exige que ela seja nula. O runtime
insere eventos somente por `audit.append_event`; o append de sucesso participa
da mesma transação da mutação de negócio.

## Deletion strategy

Dados de negócio usam soft-delete auditável. Em `foundation.approvals`, veto e
expiração marcam o motivo terminal e consumo grava resultado; nenhum caminho de
runtime recebe `DELETE`. Retenção/limpeza lógica do estado terminal é distinta
da purga física futura da issue #201.

## Migration notes

`AddFoundationApprovals` cria `foundation.approvals`, RLS forçado, policy de
isolamento por empresa/ator, grants mínimos e as constraints/índices acima.
`AddAiWritesConfirmCapability` semeia `ai.writes.confirm` com identidade
estável e impede downgrade enquanto houver grants.

A fronteira de ownership usa `vinx_migrator LOGIN NOINHERIT NOBYPASSRLS`,
`vinx_runtime LOGIN NOINHERIT NOBYPASSRLS`, `vinx_audit_owner NOLOGIN
NOINHERIT NOBYPASSRLS`, `vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS` e
`vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS`. O owner de identidade
possui somente `USAGE` em `foundation`, `SELECT` em `foundation.users` e
`UPDATE` apenas em `sessions_valid_after`; o owner de autorização recebe
somente leitura necessária para `current_actor_memberships()`. Essas roles são
distintas das roles legadas do Supabase: `anon`, `authenticated` e
`service_role`.

A revogação de membership marca somente soft-delete e auditoria técnica da
membership própria. O runtime recebe `UPDATE` apenas nas colunas de ciclo de
vida da revogação, auditoria de alteração e `row_version`; `INSERT`, `DELETE`
e chaves da membership continuam negados, sob a policy RLS de empresa.

O runtime possui `SELECT` e `INSERT` em nível de tabela e `UPDATE` somente nas
colunas de ciclo de vida, auditoria de alteração, `row_version` e soft-delete;
`DELETE` e alteração de chaves permanecem negados. O contrato de schema é
verificado em PostgreSQL real pelos testes de migração e de grants.

## Observabilidade e segurança

Todos os hosts configuram `AddVinxLogging`, que preserva `ILogger<T>` nativo e
emite console JSON com timestamp UTC ISO 8601. Hosts HTTP usam
`UseVinxRequestCorrelation` antes dos endpoints; o middleware gera o
`CorrelationId` no servidor, marca a `Activity` atual e o propaga por scope.
Auditoria é append-only, transacional com a mutação e redigida. Logs usam
templates constantes, operação, módulo, resultado e motivo seguro; nunca
registram tokens, credenciais, PII, prompts ou payload bruto. O host autentica
antes de criar contexto confiável, mas endpoints de aprovação do AI ainda não
fazem parte desta fatia.

Configuração obrigatória de banco, Supabase Auth e Storage usa Options com
`ValidateOnStart`; erro impede o host de servir tráfego sem incluir credencial
na mensagem. HTTP de provider só é aceito em loopback durante desenvolvimento;
demais ambientes exigem HTTPS. Os três hosts usam Health Checks nativo. O
`ApiHost` usa Problem Details somente para exceções não tratadas, preservando
os contratos `{ code }` e `401` vazio. O `McpHost`
reutiliza a validação JWT, mas a sonda `vinx_ping` não materializa ator nem
empresa; ferramentas de domínio continuam bloqueadas até o contexto confiável.
