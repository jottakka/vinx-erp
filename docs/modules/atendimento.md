# Atendimento

## Ownership

O módulo `Vinx.Erp.Atendimento` possui o cartão fixo de Atendimento e seu histórico imutável. Foundation fornece somente transação, RLS, auditoria, catálogo de capabilities e idempotência compartilhada.

## Company scope

Toda rota exige `companyId` explícito, membership ativo e capability Company. As tabelas `service_desk.atendimento_cards` e `service_desk.atendimento_card_history` usam RLS pela empresa da transação; o runtime não recebe DELETE. O browser usa apenas BFF/same-origin e nunca acessa PostgreSQL ou provider. Respostas protegidas usam `Cache-Control: private, no-store`.

## Contract version

MVP interno sem versão paralela. `GET /atendimento/cards?companyId=` lista a fila. `POST /atendimento/cards`, `/{id}/assign` e `/{id}/move` recebem `requestId`; atribuição e movimento exigem `expectedVersion`. As colunas fixas são `novo`, `em_andamento` e `concluido`. Quando a autenticação é pelo cookie BFF, cada mutação também exige o header `X-CSRF-TOKEN`, obtido de `GET /foundation/identity/browser-session/antiforgery`; bearer não depende desse token.

## Deletion strategy

Cartões possuem campos de soft-delete para evolução compatível, mas esta fatia não publica exclusão. Histórico de movimento é append-only e não recebe UPDATE ou DELETE do runtime.

## Migration notes

A migration `20260816030000_AddAtendimentoKanbanMvp` cria o schema `service_desk`, grants mínimos, RLS e as capabilities canônicas UUIDv5 `service_desk.ticket.read_queue`, `.create`, `.assign` e `.move`. Nome já existente preserva seu UUID legado; UUID canônico ocupado por outro nome aborta a migration e o downgrade só remove a combinação canônica não referenciada. Idempotência é autorizada antes da leitura do registro; assign e move incluem o `cardId` tanto no hash canônico quanto no escopo do registro. Replay equivalente devolve o resultado original e divergência devolve `409 idempotency_conflict`. Conflito de versão usa atualização condicional com retorno de linha ou releitura autorizada e devolve `409 integrity_conflict` com estado atual seguro, sem sobrescrever cartão ou histórico. Auditoria usa a ação do comando `service_desk.ticket.*` e mantém o motivo estável em campo separado; falha de auditoria desfaz mutação, histórico e idempotência.

Ficam fora do MVP: templates genéricos, configuração de quadro, SLA, canais WhatsApp/e-mail, IA, chatbot, roteamento, automação, MCP e integrações Paguai.
