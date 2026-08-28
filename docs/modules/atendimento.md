# Atendimento

## Ownership

O módulo `Vinx.Erp.Atendimento` possui o cartão fixo de Atendimento e seu histórico imutável. Foundation fornece somente transação, RLS, auditoria, catálogo de capabilities e idempotência compartilhada.

## Company scope

Toda rota exige `companyId` explícito, membership ativo e capability Company. As tabelas `service_desk.atendimento_cards`, `service_desk.atendimento_card_history` e as tabelas novas da fatia de usabilidade usam RLS pela empresa da transação; o runtime não recebe DELETE de cartões. O browser usa apenas BFF/same-origin e nunca acessa PostgreSQL ou provider. Respostas protegidas usam `Cache-Control: private, no-store`.

## Contract version

MVP interno sem versão paralela. `GET /atendimento/cards?companyId=` lista a fila e inclui anexo confirmado (ou nulo) e participantes em ordem canônica, e aceita filtros server-side compartilháveis por query param: `status`, `priority`, `assigneeMembershipId`, `overdue`, `search`. `GET /atendimento/assignment-options?companyId=` exige capability de atribuição e retorna somente `membershipId` e `displayName` das memberships ativas. `POST /atendimento/cards`, `/{id}/assign`, `/{id}/move`, `/{id}/cancel`, `/{id}/fields`, `/{id}/watchers/add` e `/{id}/watchers/remove` recebem `requestId`; mutações exigem `expectedVersion`.

As colunas fixas são `novo`, `em_andamento`, `concluido` e `cancelado`. Apenas o cancelamento dedicado alcança `cancelado`: ele é terminal, exige a capability dedicada `service_desk.ticket.cancel` (não reutiliza `move`) e motivo aparado entre 1 e 500 caracteres, preserva idempotência, histórico e auditoria como `service_desk.ticket.cancel`. Create aceita no máximo um `attachmentFileId` confirmado da mesma empresa com classificação `service_desk_attachment` e até 25 participantes ativos; participantes repetidos são deduplicados e ordenados pelo UUID canônico. Quando a autenticação é pelo cookie BFF, cada mutação também exige o header `X-CSRF-TOKEN`, obtido de `GET /foundation/identity/browser-session/antiforgery`; bearer não depende desse token.

## Operational metadata

Create e read expõem `priority` (`critica|alta|media|baixa`, padrão `media`) e
`channel` (`whatsapp|email|web|manual`, padrão `manual`); o formulário Web envia
`web`. `slaDueAt` é anulável. `isOverdue` é calculado no servidor, não persiste,
usa um instante UTC por operação e é sempre falso em `concluido`/`cancelado`.
`createdAt`/`updatedAt` são anuláveis para replay legado; criação e lista novas
os preenchem. A fila ordena `createdAt DESC, id DESC`.

O hash legado permanece compatível somente com defaults, sem anexo nem
participantes; qualquer metadado efetivo diferente produz conflito. A migration
`20260826225942_AddAtendimentoOperationalMetadata` cria defaults, constraints e
o índice da fila; o `Down` falha fechado se houver valor não-default. A fatia P6
abaixo relaxa `priority` para anulável (`varchar(20)` sem default) sobre essa
base: ausência significa sem prioridade definida.

## Fatia de usabilidade (P6)

- **Campos de ticket** em `service_desk.atendimento_cards`: `requester_person_ref`, `priority` (`critica|alta|media|baixa`, anulável), `category_ref_id`, `channel_ref_id` e `sla_due_at`. Todos opcionais para compatibilidade; `POST /{id}/fields` usa semântica null = inalterado. SLA no passado é rejeitado na escrita; **atraso é derivado em leitura** (`sla_due_at < now` e coluna ∉ `concluido`/`cancelado`) e nunca é estado armazenado.
- **Lookups**: categorias por empresa (`atendimento_categories`) administradas por quem tem `service_desk.lookup.admin` (criar idempotente, desativar); duplicata devolve `409 lookup_conflict`. Canais (`whatsapp/email/web/manual`) são semeados por migração via backfill mais trigger em `foundation.companies` e ficam somente leitura para o runtime.
- **Watchers**: tabela `atendimento_card_watchers` (membership da empresa); adicionar/remover exige `service_desk.ticket.watchers`, faz bump otimista do cartão e repetição devolve `200` sem alterar versão (`NoChange`). Sem caminho de exclusão física de cartões.
- **Timeline**: `service_desk.atendimento_card_events` recebe entradas append-only `{actor, action_kind, occurred_at}` com digest SHA-256 do payload nos eventos `created|assigned|moved|fields_updated|watcher_added|watcher_removed`. A leitura `GET /atendimento/cards/{id}/events` devolve os últimos 100 em ordem crescente. A ordem canônica é `event_seq` — identidade monotônica da transação de escrita — e os tipos incluem `mentioned` para menções (além de `fields_updated` para os demais campos no mesmo comando). Nome do ator é resolvido no servidor (membership → usuário). A tabela legada `atendimento_card_history` continua sendo escrita nos movimentos para compatibilidade.
- **Capabilities novas**: `service_desk.ticket.fields`, `service_desk.ticket.watchers`, `service_desk.lookup.admin` e, em Foundation, `directory.read` (leitura de diretório para pickers deliberadamente menor que `foundation.employees.create`).
- **Notificações**: atribuição, menções e watcher adicionado geram intents na seam pública existente (`NotificationIntentService.EnqueueAsync`, schema `notifications`). Como o serviço abre transação própria, o envio ocorre **pós-commit** e apenas em terminal de sucesso (Success/Created): rollback de auditoria, autorização ou idempotência nunca notifica. Chave determinística com cartão e tipo na frente — `atm.card.<cardId>.<tipo>`; o destinatário não entra na chave porque a unicidade do intent já inclui `recipient_user_id`. Falha é best-effort registrada em Warning e jamais desfaz a mutação.
- **Pré-requisito de implantação (templates)**: a migração P6 não toca o schema `notifications` (isolamento por precedência WhatsApp/SMTP). O operador insere por empresa as linhas `in_app` aprovadas antes de habilitar consumo: `atendimento.card.assigned`, `atendimento.card.mentioned` e `atendimento.card.watcher_added` em `notifications.notification_templates` com `is_approved = true`. Enquanto o template não existe, o enqueue falha seguro (intent é simplesmente pulado com Warning) e nenhuma mutação de cartão é afetada.
- **Pickers**: `GET /foundation/companies/{companyId}/directory-options` expõe membros (IDs opacos + nome) sob `directory.read`; o front monta avatar por iniciais e guarda apenas membership-id internamente.

Ficam fora da fatia: escalar/renotificar por SLA, templates traduzidos, drag-and-drop adicional (o quadro já possui movimento nativo e permanece intocado), exclusão de cartões e edição dos canais semeados. Paginação, outbox e IA permanecem fora do contrato atual.

## Deletion strategy

Cartões possuem campos de soft-delete para evolução compatível, mas esta fatia não publica exclusão. Histórico de movimento e eventos de timeline são append-only e não recebem UPDATE ou DELETE do runtime; remover watcher remove somente o vínculo. Categorias desativam por `is_active`. Participantes usam RLS por empresa e concedem ao runtime somente `SELECT` e `INSERT`.

## Migration notes

A migration `20260816030000_AddAtendimentoKanbanMvp` cria o schema `service_desk`, grants mínimos, RLS e as capabilities canônicas UUIDv5 `service_desk.ticket.read_queue`, `.create`, `.assign` e `.move`; a `20260826152000_AddServiceDeskTicketCancel` adiciona `service_desk.ticket.cancel` com as mesmas salvaguardas. Nome já existente preserva seu UUID legado; UUID canônico ocupado por outro nome aborta a migration e o downgrade só remove a combinação canônica não referenciada. A evolução de cancelamento preserva a chave alternativa real `(company_id, id)`, adiciona FK composta de anexo para `foundation.files`, participantes com FKs compostas e a função `foundation.current_company_membership_options()` `SECURITY DEFINER`. Ela usa o owner de autorização, `search_path` fixo e leitura limitada de `id`/`display_name`; o runtime não recebe leitura direta de `foundation.users`.

Idempotência é autorizada antes da leitura do registro; assign, move e cancel incluem o `cardId` no escopo. Create sem anexo nem participantes conserva o hash legado; entradas novas usam IDs deduplicados e ordenados. Replay equivalente devolve o resultado original e divergência devolve `409 idempotency_conflict`. Conflito de versão ou ação distinta em cartão cancelado devolve `409 integrity_conflict` com estado atual seguro, sem sobrescrever cartão ou histórico; cancelar um cartão já cancelado com um novo `requestId` devolve `409 terminal_state` nomeando a condição terminal, enquanto o replay do mesmo `requestId` devolve o resultado original. Falha de histórico, receipt ou auditoria desfaz a mutação na mesma transação. O downgrade recusa estado novo ainda referenciado.

A migration `20260827000000_AddAtendimentoTicketing` adiciona as tabelas de lookups/watchers/events, as colunas nullable de ticket e as quatro capabilities do catálogo (UUIDv5 por nome). Sobre a base do `20260826225942` do main, ela passa a apenas ALTERAR `priority` (relaxa para `varchar(20)` anulável sem default, mantendo o mesmo CHECK) em vez de recriar a coluna; `sla_due_at`/`channel` já existem. Os canais são semeados por backfill mais trigger em `foundation.companies` (que define o contexto GUC antes da escrita, mesma disciplina do provisionamento), e nada do schema `notifications` é criado ou populado pela migração.

Ficam fora do MVP: templates genéricos, configuração de quadro, motor de calendário/SLA,
integrações de canais inbound (WhatsApp/e-mail), IA, chatbot, roteamento, automação,
MCP e integrações Paguai.
