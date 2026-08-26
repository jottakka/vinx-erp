# Atendimento operacional — prioridade, canal e prazo

## Status e entrega

Desenho aprovado em 26 de agosto de 2026, issue [#334](https://github.com/jottakka/vinx-erp/issues/334), filha de #50.
A API é empilhada sobre `f57d952`; a PR web sobre a API. Ambas independem do Agente/OCR.

## Contrato

- `priority`: `critica | alta | media | baixa`; default servidor `media`.
- `channel`: `whatsapp | email | web | manual`; default servidor `manual` e
  default do formulário Web `web`.
- `slaDueAt`: instante opcional; passado é válido e representa trabalho já
  atrasado.
- Resposta: campos anteriores mais `priority`, `channel`, `slaDueAt`,
  `isOverdue`, `createdAt` e `updatedAt`.
- Fila nova sempre preenche timestamps. Receipt legado pode retorná-los `null`
  porque o JSON anterior não os preservou.
- `isOverdue` usa um único instante UTC por operação, nunca é persistido e é
  falso em `concluido` ou `cancelado`.
- Ordem da fila: `createdAt DESC, id DESC`; o envelope atual não muda.

Banco: `priority varchar(10) NOT NULL DEFAULT media`, `channel varchar(12) NOT NULL DEFAULT manual` e
`sla_due_at timestamptz NULL`, checks fechados e índice parcial `(company_id, created_at DESC, id DESC)`.
`Up` preserva linhas antigas; `Down` vê todas apesar de FORCE RLS e recusa metadado não padrão antes de DDL.

## Compatibilidade e segurança

Create aceita os campos opcionais; `null` usa defaults, enquanto vazio,
espaçado, desconhecido ou timestamp malformado recebe `400 invalid_request`.
Hash legado existe somente sem anexo/participantes e com
`media`/`manual`/SLA nulo. Mesmo `requestId` com qualquer metadado diferente
recebe `409`; replay igual não duplica cartão, receipt nem auditoria.

Permanecem: empresa explícita, membership/capability Company, FORCE RLS,
antiforgery BFF, auditoria/receipt na mesma transação, no-store, soft delete e
terminalidade de `cancelado`. A migration não concede capabilities.

## UX e exclusões

MUI existente: selects `Prioridade` e `Canal`, `Prazo de SLA`
`datetime-local`, rótulos pt-BR, `CompanyIdentity`, prazo e `Atrasado` por texto
e tom, sem UUIDs ou URLs privadas. Prioridade usa Crítica/error, Alta/warning,
Média/info e Baixa/default.

Ficam em #50: update pós-criação, filtros/paginação, categoria company-scoped,
timeline/atores, notificações/outbox, requester externo, calendário/engine de
SLA e configuração de Kanban. AI/OCR, Pagaí, canais inbound e WorkerHost não
pertencem a esta entrega.

## Evidência e limites

BDD completo está na #334 e o mapa executável em `docs/verification/atendimento-kanban-mvp-test-cases.md`.
Database/EF guards, builds e web gates são proporcionais; Sol revisa cada PR. Limites: 25 arquivos/800 linhas,
com gerados à parte. `quality/private` e providers externos permanecem fora.
