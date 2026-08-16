# Atendimento

## Ownership

O módulo `Vinx.Erp.Atendimento` possui o cartão fixo de Atendimento e seu histórico imutável. Foundation fornece somente transação, RLS, auditoria, catálogo de capabilities e idempotência compartilhada.

## Company scope

Toda rota exige `companyId` explícito, membership ativo e capability Company. As tabelas `service_desk.atendimento_cards` e `service_desk.atendimento_card_history` usam RLS pela empresa da transação; o runtime não recebe DELETE. O browser usa apenas BFF/same-origin e nunca acessa PostgreSQL ou provider.

## Contract version

MVP interno sem versão paralela. `GET /atendimento/cards?companyId=` lista a fila. `POST /atendimento/cards`, `/{id}/assign` e `/{id}/move` recebem `requestId`; atribuição e movimento exigem `expectedVersion`. As colunas fixas são `novo`, `em_andamento` e `concluido`.

## Deletion strategy

Cartões possuem campos de soft-delete para evolução compatível, mas esta fatia não publica exclusão. Histórico de movimento é append-only e não recebe UPDATE ou DELETE do runtime.

## Migration notes

A migration `20260816030000_AddAtendimentoKanbanMvp` cria o schema `service_desk`, grants mínimos, RLS e as capabilities canônicas `service_desk.ticket.read_queue`, `.create`, `.assign` e `.move`. UUID canônico ocupado por nome divergente aborta a migration. Idempotência é autorizada antes da leitura do registro; replay equivalente devolve o resultado original e divergência devolve `409 idempotency_conflict`. Conflito de versão devolve `409 integrity_conflict` com estado atual seguro, sem sobrescrever cartão ou histórico.

Ficam fora do MVP: templates genéricos, configuração de quadro, SLA, canais WhatsApp/e-mail, IA, chatbot, roteamento, automação, MCP e integrações Paguai.
