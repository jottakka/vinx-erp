# Atendimento

## Ownership

O módulo `Vinx.Erp.Atendimento` possui o cartão fixo de Atendimento e seu histórico imutável. Foundation fornece somente transação, RLS, auditoria, catálogo de capabilities e idempotência compartilhada.

## Company scope

Toda rota exige `companyId` explícito, membership ativo e capability Company. As tabelas `service_desk.atendimento_cards`, `service_desk.atendimento_card_history` e as tabelas novas da fatia de usabilidade usam RLS pela empresa da transação; o runtime não recebe DELETE de cartões. O browser usa apenas BFF/same-origin e nunca acessa PostgreSQL ou provider. Respostas protegidas usam `Cache-Control: private, no-store`.

## Contract version

MVP interno sem versão paralela. `GET /atendimento/cards?companyId=` lista a fila e aceita filtros server-side compartilháveis por query param: `status`, `priority`, `assigneeMembershipId`, `overdue`, `search`. `POST /atendimento/cards`, `/{id}/assign`, `/{id}/move`, `/{id}/fields`, `/{id}/watchers/add` e `/{id}/watchers/remove` recebem `requestId`; mutações exigem `expectedVersion`. As colunas fixas são `novo`, `em_andamento` e `concluido`. Quando a autenticação é pelo cookie BFF, cada mutação também exige o header `X-CSRF-TOKEN`, obtido de `GET /foundation/identity/browser-session/antiforgery`; bearer não depende desse token.

## Fatia de usabilidade (P6)

- **Campos de ticket** em `service_desk.atendimento_cards`: `requester_person_ref`, `priority` (`critica|alta|media|baixa`), `category_ref_id`, `channel_ref_id` e `sla_due_at`. Todos opcionais para compatibilidade; `POST /{id}/fields` usa semântica null = inalterado. SLA no passado é rejeitado na escrita; **atraso é derivado em leitura** (`sla_due_at < now` e coluna ≠ `concluido`) e nunca é estado armazenado.
- **Lookups**: categorias por empresa (`atendimento_categories`) administradas por quem tem `service_desk.lookup.admin` (criar idempotente, desativar); duplicata devolve `409 lookup_conflict`. Canais (`whatsapp/email/web/manual`) são semeados por migração via backfill mais trigger em `foundation.companies` e ficam somente leitura para o runtime.
- **Watchers**: tabela `atendimento_card_watchers` (membership da empresa); adicionar/remover exige `service_desk.ticket.watchers`, faz bump otimista do cartão e repetição devolve `200` sem alterar versão (`NoChange`). Sem caminho de exclusão física de cartões.
- **Timeline**: `service_desk.atendimento_card_events` recebe entradas append-only `{actor, action_kind, occurred_at}` com digest SHA-256 do payload nos eventos `created|assigned|moved|fields_updated|watcher_added|watcher_removed`. A leitura `GET /atendimento/cards/{id}/events` devolve os últimos 100 em ordem crescente com nome do ator resolvido no servidor (membership → usuário). A tabela legada `atendimento_card_history` continua sendo escrita nos movimentos para compatibilidade.
- **Capabilities novas**: `service_desk.ticket.fields`, `service_desk.ticket.watchers`, `service_desk.lookup.admin` e, em Foundation, `directory.read` (leitura de diretório para pickers deliberadamente menor que `foundation.employees.create`).
- **Notificações**: atribuição, menções e watcher adicionado geram intents na seam pública existente (`NotificationIntentService.EnqueueAsync`, schema `notifications`). Como o serviço abre transação própria, o envio ocorre **pós-commit** com chave determinística (`templateKey:cardId:recipientUserId`) para dedupe downstream; falha é best-effort registrada em Warning e jamais desfaz a mutação.
- **Pickers**: `GET /foundation/companies/{companyId}/directory-options` expõe membros (IDs opacos + nome) sob `directory.read`; o front monta avatar por iniciais e guarda apenas membership-id internamente.

## Deletion strategy

Cartões possuem campos de soft-delete para evolução compatível, mas esta fatia não publica exclusão. Histórico de movimento e eventos de timeline são append-only e não recebem UPDATE ou DELETE do runtime; remover watcher remove somente o vínculo. Categorias desativam por `is_active`.

## Migration notes

A migration `20260816030000_AddAtendimentoKanbanMvp` cria o schema `service_desk`, grants mínimos, RLS e as capabilities canônicas UUIDv5 `service_desk.ticket.read_queue`, `.create`, `.assign` e `.move`. Nome já existente preserva seu UUID legado; UUID canônico ocupado por outro nome aborta a migration e o downgrade só remove a combinação canônica não referenciada. Idempotência é autorizada antes da leitura do registro; assign e move incluem o `cardId` tanto no hash canônico quanto no escopo do registro. Replay equivalente devolve o resultado original e divergência devolve `409 idempotency_conflict`. Conflito de versão usa atualização condicional com retorno de linha ou releitura autorizada e devolve `409 integrity_conflict` com estado atual seguro, sem sobrescrever cartão ou histórico. Auditoria usa a ação do comando `service_desk.ticket.*` e mantém o motivo estável em campo separado; falha de auditoria desfaz mutação, histórico e idempotência.

A migration `20260827000000_AddAtendimentoTicketing` adiciona as tabelas de lookups/watchers/events, as colunas nullable de ticket, as quatro capabilities do catálogo (UUIDv5 por nome) e semeia canais e templates in-app aprovados (`atendimento.card.assigned/mentioned/watcher_added`) por empresa, cobrindo também empresas futuras pelo trigger. Empresas criadas depois da migração recebem os mesmos dados pelo trigger; nenhum serviço de provisionamento foi alterado.

Ficam fora da fatia: escalar/renotificar por SLA, templates traduzidos, drag-and-drop adicional (o quadro já possui movimento nativo e permanece intocado), exclusão de cartões e edição dos canais semeado.
