# Atendimento

## Ownership

O módulo `Vinx.Erp.Atendimento` possui o cartão fixo de Atendimento e seu histórico imutável. Foundation fornece somente transação, RLS, auditoria, catálogo de capabilities e idempotência compartilhada.

## Company scope

Toda rota exige `companyId` explícito, membership ativo e capability Company. As tabelas `service_desk.atendimento_cards` e `service_desk.atendimento_card_history` usam RLS pela empresa da transação; o runtime não recebe DELETE. O browser usa apenas BFF/same-origin e nunca acessa PostgreSQL ou provider. Respostas protegidas usam `Cache-Control: private, no-store`.

## Contract version

MVP interno sem versão paralela. `GET /atendimento/cards?companyId=` lista a fila e inclui anexo confirmado (ou nulo) e participantes em ordem canônica. `GET /atendimento/assignment-options?companyId=` exige capability de atribuição e retorna somente `membershipId` e `displayName` das memberships ativas. `POST /atendimento/cards`, `/{id}/assign`, `/{id}/move` e `/{id}/cancel` recebem `requestId`; atribuição, movimento e cancelamento exigem `expectedVersion`.

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
o índice da fila; o `Down` falha fechado se houver valor não-default.

Esta fatia não inclui filtros, paginação, categoria, update, timeline, outbox ou
IA; esses itens permanecem fora do contrato atual.

## Deletion strategy

Cartões possuem campos de soft-delete para evolução compatível, mas esta fatia não publica exclusão. Histórico de movimento é append-only e não recebe UPDATE ou DELETE do runtime. Participantes usam RLS por empresa e concedem ao runtime somente `SELECT` e `INSERT`.

## Migration notes

A migration `20260816030000_AddAtendimentoKanbanMvp` cria o schema `service_desk`, grants mínimos, RLS e as capabilities canônicas UUIDv5 `service_desk.ticket.read_queue`, `.create`, `.assign` e `.move`; a `20260826152000_AddServiceDeskTicketCancel` adiciona `service_desk.ticket.cancel` com as mesmas salvaguardas. Nome já existente preserva seu UUID legado; UUID canônico ocupado por outro nome aborta a migration e o downgrade só remove a combinação canônica não referenciada. A evolução de cancelamento preserva a chave alternativa real `(company_id, id)`, adiciona FK composta de anexo para `foundation.files`, participantes com FKs compostas e a função `foundation.current_company_membership_options()` `SECURITY DEFINER`. Ela usa o owner de autorização, `search_path` fixo e leitura limitada de `id`/`display_name`; o runtime não recebe leitura direta de `foundation.users`.

Idempotência é autorizada antes da leitura do registro; assign, move e cancel incluem o `cardId` no escopo. Create sem anexo nem participantes conserva o hash legado; entradas novas usam IDs deduplicados e ordenados. Replay equivalente devolve o resultado original e divergência devolve `409 idempotency_conflict`. Conflito de versão ou ação distinta em cartão cancelado devolve `409 integrity_conflict` com estado atual seguro, sem sobrescrever cartão ou histórico; cancelar um cartão já cancelado com um novo `requestId` devolve `409 terminal_state` nomeando a condição terminal, enquanto o replay do mesmo `requestId` devolve o resultado original. Falha de histórico, receipt ou auditoria desfaz a mutação na mesma transação. O downgrade recusa estado novo ainda referenciado.

Ficam fora do MVP: templates genéricos, configuração de quadro, SLA, canais WhatsApp/e-mail, IA, chatbot, roteamento, automação, MCP e integrações Paguai.
