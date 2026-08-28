# Demandas

## Ownership

O módulo `Vinx.Erp.Demandas` possui o fluxo operacional interno de demandas (o "kanban automatizado" adotado do legado 0055/0056, com vocabulário honesto). Foundation fornece transação, RLS, auditoria, catálogo de capabilities, idempotência e o serviço de intents de notificação. Aprovação consome os serviços públicos do Foundation via costura parametrizada (`ApprovalCapabilityPolicy.ForModule`) — alçada vive no Foundation, nada clonado aqui. Worker Wolverine executa varredura a cada 5 min (fila durável `demandas-automation`, tick auto-perpetuante).

## Company scope

Toda rota exige `companyId` explícito e membership ativo; a capability define o verbete. As seis tabelas em `demandas.*` usam RLS fail-closed: sem `app.company_id` na transação nenhuma linha é visível ou mutável (`FORCE ROW LEVEL SECURITY`). O browser fala apenas BFF/same-origin. Respostas protegidas usam `Cache-Control: private, no-store`.

Estágios: `initiation`, `execution`, `awaiting_approval`, `closed`. Atraso é estado DERIVADO (`overdue_since` alimentado pelo worker + cálculo por prazo efetivo), nunca uma coluna do quadro. Prazo efetivo = MIN(prazo manual, SLA da prioridade, prazo máximo da etapa). Avanço manual é comando com `requestId` + `expectedVersion`; não existe drag livre entre colunas.

Re-iniciação (voltar ao `initiation` com ciclo novo) ocorre SOMENTE quando mudam responsável, rotina, tipo ou departamento — título/descrição/prioridade/prazo jamais reiniciam o fluxo. `StageCycleCount` cresce a cada re-iniciação.

## Contract version

MVP interno sem versão paralela. Rotas sob `/demandas`:

- `GET /demandas/board?companyId=&search=&priority=&responsibleMembershipId=` — cartões derivados (`effectiveDeadline`, `isOverdue`), limite por página ≤200 (padrão 100), continuação por cursor.
- `GET /demandas/demands/{id}?companyId=` — detalhe com checklist, participantes vigentes e histórico append-only.
- `POST /demandas/demands`, `/demands/{id}/advance`, `/demands/{id}/checklist-items/complete`, `/demands/{id}/responsible`, `/demands/{id}/pause`, `/demands/{id}/resume`, `/demands/{id}/resume-overdue`, `/demands/{id}/participants`; `DELETE /demands/{id}/participants/{participantId}` (query params).
- Rotinas (CRUD capability-gated, ZERO seeds): `GET/POST /demandas/routines` e `POST /demandas/routines/{id}/status`.
- Board paginado por cursor opaco (createdAt+id base64url): `?limit=` (≤200, padrão 100) + `?after=`; envelope `{items,total,hasMore,nextCursor}` — sem truncamento silencioso.

Capabilities canônicas UUIDv5 `demandas.demand.read|create|advance|assign|participants.manage` e `demandas.routine.manage`. Troca de responsável exige criador da demanda, supervisor vigente OU capability assign — nunca identidade por nome exibido ou cargo nominal. Criação valida na fronteira do servidor que responsável e solicitante (quando informado) são memberships ATIVAS da MESMA empresa — membership desativada ou usuário de outra empresa recebe `400 invalid_request`; o auto-atributo declarado pela rotina segue a mesma fronteira. Replays equivalentes devolvem o resultado original; divergência devolve `409 idempotency_conflict`; conflito de versão devolve `409 integrity_conflict` com estado atual. Quando a autenticação é pelo cookie BFF, cada mutação também exige o header `X-CSRF-TOKEN`.

Gatilhos: cada etapa tem exatamente um gatilho (`explicit_completion`, `checklist_completed`, `time_elapsed`); conclusão manual em etapa com outro gatilho responde `409 illegal_transition`. Checklist-100% avança automaticamente quando esse é o gatilho vigente. Toda rotina precisa iniciar em `initiation`.

Notificações via intents post-commit (flush terminal-only): `assigned`/`reassigned` → novo responsável (+criador), `participant_added` → adicionado, `stage_advanced` → responsável + supervisor + observadores dedup; kinds `assigned|reassigned|participant_added|stage_advanced|awaiting_approval|overdue` e chaves `demanda.<demandId:N>.<cycle:N>.<kind>` (o `reassigned` distingue a troca de responsável do assignment inicial). Deep link da inbox para Demandas é fechado e testado ponta a ponta: o backend só projeta o seletor canônico `/demandas?demand=<uuid>` (parser fechado, formato `N` ou hifenizado) e o frontend valida o mesmo formato na allowlist da inbox e na leitura de `?demand=` em Demandas. Pré-requisito de template in-app: catálogo precisa ter templates aprovados `demanda.<kind>` antes de ativar eventos no ambiente; falha de intent registra Warning e nunca desfaz a transição.

**Approvals implemented**: entrar em `awaiting_approval` prepara a proposta durável (falha de preparo bloqueia o avanço) com fronteira por recurso — a proposta viva é única por (ator, empresa, demanda), então o mesmo ator pode ter duas demandas aguardando aprovação simultaneamente, enquanto o legado AI mantém o single-flight global por ator/empresa; `POST /demands/{id}/approval/approve|reject` confirmam/vetam com elegibilidade servidor-resolvida DEPOIS de travar a linha da demanda, exigindo a MESMA capability documentada dos dois lados (`demandas.demand.advance` em escopo company, módulo e política Foundation alinhadas); o criador da entrega NUNCA decide a própria demanda e nem supervisor nominal nem portadores apenas de `assign` decidem sem `advance`. Confirm claimed/replay encerra; veto reinicia em execution com ciclo novo. Intents: `awaiting_approval`. **Worker implemented**: o mesmo tick avança primeiro os gatilhos de tempo vencidos e só depois marca o atraso derivado (`overdue_since`), por empresa, com `FOR UPDATE SKIP LOCKED` e eventos idempotentes por `(demanda, etapa, chave-gatilho)` — replay não duplica. Intent `overdue` usa o mesmo compositor. Corte documentado: o worker NÃO avança automaticamente para `awaiting_approval` (o preparo exige ator com capability); a etapa due aguarda avanço manual.

## Deletion strategy

Runtime recebe DELETE somente para soft-delete de participante. Demandas e rotinas têm campos de compatibilidade mas esta fatia não publica exclusão; desativação de rotina cobre inativar roteamento. Histórico `demand_events` é append-only (grants INSERT/SELECT) com token de ocorrência deduplicando replay do worker.

## Migration notes

Migration SQL bruto `20260826160000_AddDemandasCore` cria schema `demandas` (routines/routine_stages/demands/participants/checklist_items/events), capabilities no catálogo, RLS fail-closed por empresa e grants mínimos; índice parcial único garante um supervisor vivo por demanda e `uq_demands_company_number` âncora o número sequencial `DEM-nnnnnn` (IDENTITY ALWAYS). Migração model-only irmã registra o mapeamento no snapshot EF. Etapas validam CHECK de vocabulário honesto, unicidade por ordem e consistência de horas por gatilho. A política de alçadas de aprovação vive no Foundation (`rh_alçadas` jamais clonada aqui).

Fora desta fatia: designer de configuração de rotinas na web, migração de dados do legado, MCP tools e engine genérica de workflow. Decisão MCP: ferramentas adiadas — o valor da fatia está no contrato interno versionado e a lista de ferramentas só faz sentido depois das fatias de aprovação e worker fecharem comportamento.

## Slices pending

- Nenhuma pendente desta sequência (core/approvals/worker entregues). O web do quadro já entrega leitura por empresa, filtros, detalhe e avanço capability-gated; designer de rotinas continua fora da fatia.
