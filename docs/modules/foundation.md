# Módulo Foundation

Foundation fornece os contratos transversais do ERP: identidade, empresas,
membership, capabilities, RLS, auditoria, idempotência e as aprovações
duráveis consumidas pelos módulos. Não contém regras de negócio de Paguai,
Service Desk, Kanban, HR, Notificações ou AI.

## Ownership

- grupos, empresas, unidades, times, usuários e memberships ativas;
- roles, capabilities nomeadas e escopos `self`, `team`, `unit`, `company` e
  `group`;
- `foundation.approvals`, seus serviços de preparação, confirmação e veto;
- identidade, auditoria, correlação, redaction, telemetria, health checks,
  idempotência e contratos técnicos de outbox.

## Company scope

Todo comando recebe uma `empresa_id` explícita e o servidor resolve o ator por
contexto autenticado. A UI apenas molda menus; API, MCP e PostgreSQL negam
contexto forjado ou cruzado. Em aprovações, RLS é forçado e combina empresa e
ator; contexto ausente falha fechado.

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

Os `RequestId` de preparação, confirmação e execução são distintos. O último é
fornecido pelo servidor ao harness e permite que o serviço de domínio preserve
seu resultado em replay; Foundation não promete exatamente-uma-vez.

## Invariantes persistentes

A migration declara, com nomes estáveis:

- constraints `ck_approvals_expires_after_creation`,
  `ck_approvals_payload_hash_format`, `ck_approvals_veto_complete`,
  `ck_approvals_consumption_complete` e `ck_approvals_terminal_exclusive`;
- índice único parcial `ix_approvals_company_id_actor_user_id`, filtrado por
  `consumed_at IS NULL AND deleted_at IS NULL`, garantindo uma proposta viva
  por ator/empresa;
- índices `ix_approvals_actor_user_id`,
  `ix_approvals_confirmation_request_id` (único e não nulo) e
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

O runtime possui `SELECT` e `INSERT` em nível de tabela e `UPDATE` somente nas
colunas de ciclo de vida, auditoria de alteração, `row_version` e soft-delete;
`DELETE` e alteração de chaves permanecem negados. O contrato de schema é
verificado em PostgreSQL real pelos testes de migração e de grants.

## Observabilidade e segurança

Auditoria é append-only, transacional com a mutação e redigida. Logs usam
`ILogger` com operação, módulo, resultado e motivo seguro; nunca registram
tokens, credenciais, PII, prompts ou payload bruto. O host autentica antes de
criar contexto confiável, mas endpoints de aprovação do AI ainda não fazem
parte desta fatia.
