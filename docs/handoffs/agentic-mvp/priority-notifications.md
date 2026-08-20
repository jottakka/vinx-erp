# Handoff — notificações: inbox in-app e contador de não lidas

## Objetivo

Entregar a funcionalidade de demo de notificações que falta depois do outbox e do worker: uma inbox in-app autorizada, paginada, com contador de não lidas e ação idempotente de marcar como lida.

Este handoff cobre o produto visível de #94/#95. Não reabre o trabalho já mergeado de outbox, worker, provider ou WhatsApp.

## Base e pré-condições

- Base obrigatória: `origin/main` atualizado. Faça `git fetch origin main --quiet` antes de começar; não assuma que o `HEAD` local já contém os PRs #243/#248.
- Trabalhar em branch `codex/*`; nunca escrever diretamente em `main`.
- Não ler, listar ou executar `quality/private`.
- Antes do código, o GPT-5.6 Sol deve revisar este handoff e aprovar o contrato de leitura/marcação, a estratégia de RLS e o limite de dados retornados.

## Estado já existente

Os seguintes blocos já existem na `origin/main` e são dependências, não escopo para reimplementar:

- #243 / `docs/superpowers/plans/2026-08-18-issue-26-notification-outbox.md`: intent, templates, preferências e outbox transacional.
- #248 / `docs/superpowers/plans/2026-08-19-issue-48-notification-worker.md`: claim, estados do worker, retry, ambiguidade, DLQ e métricas.
- `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationIntentService.cs`.
- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260818120000_AddNotificationIntentOutbox.cs`.
- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260819120000_AddNotificationDeliveryWorker.cs`.
- `src/Modules/Vinx.Erp.Notifications/` e `src/Hosts/Vinx.Erp.WorkerHost/`.

O intent já carrega apenas identificadores/opções controladas: empresa, destinatário, evento, canal, template aprovado, deep-link allowlisted, status, correlation e timestamps. Não criar uma segunda fila nem colocar conteúdo livre, telefone, e-mail, prompt, segredo ou payload bruto na resposta da inbox.

## Escopo fechado de #94/#95

### Entregar

- Backend para listar notificações `in_app` que o usuário atual pode ver, com paginação estável.
- Contador de não lidas no escopo autorizado.
- Marcação de uma notificação como lida, segura e idempotente.
- Auditoria da mudança de leitura quando exigida pelo contrato, sem conteúdo sensível.
- Rota de UI, contador no shell, lista, loading, empty, error e ação de marcar como lida.
- Deep-link somente por allowlist conhecida; a UI não deve navegar para URL arbitrária armazenada no banco.

### Não entregar neste corte

- WhatsApp, Meta, templates externos, provider, webhook, push, realtime, polling contínuo ou e-mail.
- Alterar claim/retry/DLQ/metrics do worker.
- Preferências de canal ou opt-out.
- Criar notificações de negócio novas dentro deste PR.
- Reabrir o Paguai ou emitir evento novo para fazer a inbox parecer cheia.
- Retornar corpo de mensagem livre, destinatário, telefone, e-mail, prompt, segredo ou payload de outbox.

## Contexto que o Sol precisa revisar antes do código

O agente deve enviar ao GPT-5.6 Sol este handoff e ler, na ordem abaixo:

1. `AGENTS.md`
2. `docs/modules/notifications.md`
3. `docs/adr/0006-outbox-postgresql.md`
4. `docs/superpowers/plans/2026-08-18-issue-26-notification-outbox.md`
5. `docs/superpowers/plans/2026-08-19-issue-48-notification-worker.md`
6. `docs/verification/issue-26-notification-outbox-test-cases.md`
7. `docs/verification/issue-48-notification-worker-test-cases.md`
8. `docs/contracts/openapi/README.md`
9. `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationIntentService.cs`
10. `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationChannel.cs`
11. `src/Foundation/Vinx.Erp.Foundation/Notifications/NotificationDeliveryKey.cs`
12. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260818120000_AddNotificationIntentOutbox.cs`
13. `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260819120000_AddNotificationDeliveryWorker.cs`
14. `apps/web/src/app/app-shell.tsx`
15. `apps/web/src/app/shell/app-header.tsx`
16. `apps/web/src/app/app-route-manifest.tsx`
17. `gh issue view 94 --repo jottakka/vinx-erp`
18. `gh issue view 95 --repo jottakka/vinx-erp`

### Prompt copiável para o Sol

> Revise este handoff de notificações como reviewer crítico antes de qualquer código. Leia todos os arquivos e issues listados. Valide read model, RLS, empresas autorizadas, grants, `SECURITY DEFINER`, paginação, contador, marcação idempotente, concorrência, auditoria, allowlist de campos/deep-links e ausência de PII/segredos. Preserve o outbox/worker já mergeado. Retorne `APROVADO`, `APROVADO COM ALTERAÇÕES` ou `BLOQUEADO`, com decisões concretas, riscos e arquivos que exigem atenção. Não use conteúdo real de notificações em exemplos.

O pedido ao Sol deve exigir:

- `APROVADO`, `APROVADO COM ALTERAÇÕES` ou `BLOQUEADO`.
- Escolha da fonte de leitura: tabela/read model existente ou extensão mínima da intent; não duplicar estado sem justificativa.
- Definição de `readAt`/ator de leitura e garantia de idempotência em concorrência.
- Regra de autorização: usuário atual e empresas autorizadas derivadas no servidor; nenhum `companyId` vindo do cliente pode ampliar o escopo.
- RLS, função `SECURITY DEFINER`, grants e contexto transacional, mantendo o runtime sem DDL e sem DML direto fora da fronteira aprovada.
- Allowlist de campos de resposta e de deep-links; confirmação de que não há PII ou segredo em logs/auditoria/métricas.
- Cursor/limite, ordenação estável, comportamento de notificação apagada/indisponível e semântica do contador.
- Confirmação de que o worker/outbox não será alterado e de que in-app continua obrigatório quando um intent é produzido.
- Correções críticas no plano, riscos restantes e critério de parada.

Sem aprovação do Sol, o agente pode apenas mapear o código e escrever a matriz de contrato; não pode criar migration, função SQL, endpoint ou UI.

## Contrato proposto para revisão — não implementar sem aprovação

Proposta de menor superfície, sujeita ao Sol:

```text
GET  /notifications/inbox?cursor=<opaque>&limit=<1..50>
GET  /notifications/inbox/unread-count
POST /notifications/inbox/{deliveryId}/read
```

Resposta de item candidata:

```json
{
  "id": "00000000-0000-0000-0000-000000000000",
  "companyId": "00000000-0000-0000-0000-000000000000",
  "source": "Paguai",
  "event": "approval_required",
  "createdAt": "2026-08-19T12:00:00Z",
  "readAt": null,
  "action": { "kind": "paguai", "href": "/paguai" }
}
```

Regras candidatas:

- `id` e `cursor` são opacos; não aceitar lista de empresas, destinatário ou usuário no query string.
- O servidor calcula empresas autorizadas pelo ator atual e aplica o filtro a cada leitura e marcação.
- `source`, `event` e `action` devem vir de allowlists/normalização segura; nunca renderizar `template_key` ou `deep_link_selector` arbitrário como HTML/URL.
- Repetir `POST .../read` retorna sucesso idempotente ou o mesmo resultado de “já lida”, sem duplicar auditoria de mudança.
- Item fora do escopo não vira `404` indistinguível de falha interna por acidente; usar o mapeamento de erro aprovado pelo Sol sem vazar existência entre tenants.
- A contagem considera apenas `in_app`, o estado de leitura e o escopo autorizado atual.

Se o contrato real precisar de envelope/cursor diferente, o Sol deve decidir antes dos testes RED e o agente deve registrar a decisão na issue #94/#95.

## Roteamento de modelos

- **GPT-5.6 Luna / Codex CLI:** inventário, testes RED/GREEN repetitivos, fixtures sintéticas, SQL/migration mecânico conforme desenho aprovado, testes de paginação, documentação e gates.
- **GPT-5.6 Sol:** review do plano, schema/read model, RLS, autorização por empresa, marcação idempotente, concorrência, auditoria, allowlist de dados/deep-links e review final do diff real.
- **GPT-5.6 Terra:** UI da inbox, contador, loading/empty/error, foco/teclado, labels e testes de apresentação. Terra não decide contrato, tenant, SQL ou segurança.

## Fronteira de arquivos

### Este agente é dono de

- `src/Foundation/Vinx.Erp.Foundation/Notifications/` — contratos/serviço de leitura, sem quebrar `NotificationIntentService`.
- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/` — migration SQL da read state/read model da inbox, sem alterar as migrations já mergeadas; preferir SQL da fronteira de notificações para não disputar o snapshot EF do agente de pagamento.
- `src/Hosts/Vinx.Erp.ApiHost/Controllers/NotificationsController.cs`
- `src/Hosts/Vinx.Erp.ApiHost/Models/` — somente modelos HTTP da inbox.
- `tests/public/Vinx.Erp.Foundation.IntegrationTests/` — testes focados de #94; se a divisão for necessária, manter o nome/escopo claro.
- `apps/web/src/features/notifications/` — client, página, tipos e testes.
- `apps/web/src/app/app-route-manifest.tsx`
- `apps/web/src/app/app-shell.tsx`
- `apps/web/src/app/shell/app-header.tsx`
- `docs/modules/notifications.md`
- `docs/contracts/openapi/README.md` — somente a seção de Notifications; a integração desse arquivo será mergeada de forma serial.
- `docs/verification/issue-94-notification-inbox-test-cases.md`
- `docs/verification/issue-95-notification-inbox-ui-test-cases.md`

### Não tocar

- `src/Foundation/Vinx.Erp.Foundation/Domain/PaguaiRequest.cs`
- `src/Modules/Vinx.Erp.Paguai/`
- `apps/web/src/features/paguai/`
- `src/Modules/Vinx.Erp.Notifications/` provider/worker, salvo se o Sol encontrar um contrato quebrado real que exija correção separada.
- `src/Hosts/Vinx.Erp.WorkerHost/`
- `20260818120000_AddNotificationIntentOutbox.cs` e `20260819120000_AddNotificationDeliveryWorker.cs`.
- `Directory.Packages.props`, solução, CI e arquivos de WhatsApp.

Não alterar `Program.cs` se a descoberta existente registrar controllers automaticamente. Se for indispensável, parar e coordenar com o agente de pagamento/coordenador antes de editar arquivo compartilhado.

## Tasks

### 1. Preparar e congelar o contrato

- Fazer fetch da `origin/main`, confirmar que os PRs #243/#248 estão presentes e abrir/atualizar a execução de #94/#95.
- Obter o review do Sol e anexar decisões duráveis na issue/PR; não copiar conteúdo de notificações reais.
- Estimar o diff. Se passar de 25 arquivos não gerados ou de aproximadamente 800 linhas manuais, dividir backend #94 e UI #95; a UI só começa contra o contrato congelado.

### 2. Escrever testes RED

Cobrir as cinco classes BDD:

- **Sucesso:** notificação `in_app` existente aparece para ator autorizado, com paginação, empresa de origem, evento, data, leitura e ação segura; contador acompanha o resultado.
- **Validação:** cursor inválido, limite fora da faixa, id inválido e request malformado retornam erro seguro; marcar já lida não cria outra mudança.
- **Acesso:** usuário não membro, empresa forjada, delivery de outra empresa, membership revogada e rota de deep-link não permitida não vazam dados nem alteram estado.
- **Estado/exceção:** `whatsapp` não aparece como inbox in-app; falha de auditoria/persistência não deixa marcação parcial; worker/outbox continuam inalterados.
- **Concorrência/idempotência:** duas marcações simultâneas resultam em uma leitura consistente; contador não fica negativo; paginação estável não duplica item nem perde item elegível.

### 3. Implementar a fronteira de persistência/API

- Reusar a separação existente: Foundation guarda função/contrato de dados; ApiHost traduz HTTP; Notifications provider/worker continua fora do corte.
- Implementar a read state/read model com função SQL `SECURITY DEFINER` e grants mínimos conforme revisão do Sol.
- Aplicar contexto do ator/correlation no início de cada operação; usar consultas parametrizadas e retorno allowlisted.
- Garantir que leitura e marcação nunca confiem em `companyId`, `recipientUserId` ou permissões fornecidos pelo cliente.
- Auditar somente transições efetivas, com identificadores opacos e sem corpo/template/payload.
- Expor cursor e limite com contrato estável, ordenação determinística e `private,no-store`.

### 4. Implementar a UI

- Criar `/notificacoes` no manifest e uma entrada visível na navegação.
- Adicionar contador de não lidas no header sem fazer polling/realtime; buscar no carregamento/refresh definido pelo contrato.
- Renderizar lista paginada com `source`, evento seguro, data, estado lida/não lida e ação allowlisted.
- Marcar como lida de forma otimista apenas se rollback/error state estiverem definidos; manter idempotência no servidor.
- Cobrir teclado, foco, aria-label, contraste e estados loading/empty/error.

### 5. Documentar e verificar

- Atualizar `docs/modules/notifications.md`, contrato OpenAPI e os dois manifestos de teste.
- Rodar testes focados de SQL/Foundation/API/UI e depois `./scripts/verify-public.sh` antes do push.
- Pedir ao Sol o review final do diff real, especialmente migration SQL, grants/RLS, consulta de tenant, marcação concorrente, contador e allowlist de links.

## Critério de aceite

Um usuário autorizado abre a inbox, vê somente notificações in-app das empresas que pode acessar, recebe contador correto, pagina sem duplicação e marca um item como lido repetindo a ação sem efeitos duplicados. Nenhum provider externo, WhatsApp, worker ou Paguai é alterado.

## Paralelismo e merge

Este handoff pode executar em paralelo com `priority-payment.md`: a inbox não depende de uma notificação nova do Paguai e o pagamento não toca os arquivos de notificações/UI shell. O merge deve ser serial quando houver conflito em `docs/contracts/openapi/README.md`, `Program.cs` ou outro arquivo compartilhado detectado pelo gate.
