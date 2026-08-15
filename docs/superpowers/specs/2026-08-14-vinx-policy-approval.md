# Política e aprovação VINX (issue #127, PR 2)

## Status

Esta decisão entrega a integração durável de política/aprovação: Foundation
persiste e consome aprovações de uso único; AI coordena confirmação, veto e
single-flight. A fatia é exercida por serviços autorizados, PostgreSQL real e
o framework real, sem host HTTP novo.

## Decisões

1. `HarnessConfirmationCoordinator` mantém o registro local por `proposalId`
   opaco, mas Foundation é a autoridade de autorização, claim, veto, auditoria
   e replay.
2. Endpoints `/ai/approvals/*` ficam fora desta fatia; o fluxo é coberto por
   serviços e coordenador em testes públicos autorizados.
3. Ator e empresa vêm do contexto confiável do servidor. O cenário de host não
   autenticado é N/A e está vinculado à issue #138; `GET /ai/whoami` permanece
   na issue #125.
4. Preparação carrega empresa, `RequestId`, ação, payload e policy version;
   confirmação carrega empresa, ApprovalId, ProposalId, `RequestId`, payload e
   policy version. Nenhum desses valores transforma o modelo em autoridade.
5. A política/allowlist é do módulo AI; armazenamento, autorização durável,
   RLS, auditoria e idempotência genérica são da Foundation.
6. Não existe promessa exatamente uma vez: aprovação é single-use e a escrita
   do domínio tem idempotência própria pelo `RequestId` de execução.
7. **Exceção de arquivos aprovada pelo dono:** a fatia excede 25 caminhos.
   **Exceção de tamanho aprovada pelo dono em 15/08/2026:** PR 2 permanece
   uma única fatia vertical, com o inventário efetivo e a classificação abaixo
   substituindo a estimativa anterior. A coesão é o ciclo indivisível
   prepare→confirm/veto→retomada→execução, seus invariantes de banco e suas
   provas de concorrência; não haverá divisão nesta PR.
8. Cinco classes BDD são materializadas por xUnit e pelo manifesto público,
   sem expandir Reqnroll.
9. Segunda preparação viva do mesmo ator/empresa é `proposal_conflict`
   auditado; nunca substitui silenciosamente a proposta anterior.

## Garantias verificadas

O único feixe público é `HarnessAgentBundle`, com `CreateSessionAsync` e
`RunUserTurnAsync`. Ele esconde agente cru, fonte de `RequestId` e entrada de
aprovação. O framework real executa o turno; o coordenador não invoca a
ferramenta manualmente. O permit interno é infalsificável fora do assembly,
vinculado a `proposalId + CallId` e de uso único. O contexto confiável e o
`RequestId` de execução são criados pelo servidor.

Cada prepare, confirm e veto reavalia autorização durável. Replay com o mesmo
pedido só adota o resultado quando empresa, ator, payload, policy version e
identificadores coincidem. Foundation usa RLS por empresa/ator e o relógio do
PostgreSQL para claim, veto e expiração; falha de auditoria faz rollback.

Após o claim, a tarefa de execução é publicada sob lock e iniciada fora do
lock. `VetoStart` fornece a mesma ordenação para veto. Terminalidade local é
retida até esperadores, execução/veto e TTL terminarem; o sweep publica cleanup
antes de remover a entrada e dispõe recursos fora do lock. O shutdown fecha a
porta, quiesce o timer, drena de forma limitada e impede entrega tardia.

## RequestId e pós-crash

| Escopo | Garantia |
|---|---|
| preparação | Mesmo payload reusa a aprovação; payload divergente é `idempotency_conflict`. |
| confirmação | Um claim é `Claimed`; mesmo pedido é `Replay`; outro pedido após consumo é `Conflict` auditado. |
| execução | UUID do servidor permanece a chave de idempotência do domínio e pode recuperar o resultado original. |

Se houver timeout entre o commit durável e a observação local, somente o mesmo
`RequestId` pode adotar a propriedade e compartilhar uma conclusão estável.
Outro pedido não adota. Reinício do processo perde o registro local e retorna
`ProposalUnavailable`; não se promete replay de `AgentResponse` entre processos.

## Foundation e schema

`foundation.approvals` possui constraints
`ck_approvals_expires_after_creation`, `ck_approvals_payload_hash_format`,
`ck_approvals_veto_complete`, `ck_approvals_consumption_complete` e
`ck_approvals_terminal_exclusive`. Os índices são
`ix_approvals_company_id_actor_user_id` (único parcial para proposta viva),
`ix_approvals_actor_user_id`, `ix_approvals_confirmation_request_id` (único
parcial), `ix_approvals_proposal_id` (único) e
`ix_idempotency_records_company_id_actor_user_id_command_name_r`.

O classifier só trata os dois índices pinados como `LiveProposal` e
`Idempotency`; outro nome é `Other`. O sweep de expiração usa o instante
inicial da transação e ocorre antes do teste de proposta viva. Veto marca
`approval_vetoed`, expiração marca `approval_expired`, consumo grava resultado;
constraints impedem veto e consumo simultâneos. A purga física continua na
issue #201.

## Manifesto e evidências

As cinco classes BDD, os oito bloqueios e os comandos exatos estão em
[`docs/verification/issue-127-vinx-policy-test-cases.md`](../../verification/issue-127-vinx-policy-test-cases.md).
As contagens finais verificadas são: AI 44/44, wired PostgreSQL 6/6,
`ApprovalServicesIntegrationTests` 32/32, migrations 6/6, Foundation unit
118/118 e arquitetura pública 17/17. Formatação é verificada por
`dotnet format whitespace --verify-no-changes`.

## Hardening explicitamente deferido

Não são falhas nem blockers desta PR: matriz de trigger/ordem forçada e
advisory locks; contagens exatas de descarte de CTS/timers por instância;
instrumentação privada de ordem; reflexão ampla sobre privados; agente
sintético grande para reentrância; combinações exaustivas de timer/sweep/
dispose/execução/veto; stress, soak e repetição grande; probes que não
detectariam bypass de autorização, acesso cross-company, escrita duplicada,
claim/veto/recuperação errados, corrupção PostgreSQL, entrega pós-shutdown ou
falha do gate público. O pequeno ajuste de `VetoStart` é parte entregue.

## Inventário de arquivos e contagem de linhas

O inventário é calculado contra `f6aaded` após as edições e deve conter
exatamente 46 caminhos. Linhas manuais, geradas e de lockfile são separadas
pelos números efetivos de `git diff --numstat`; nenhuma estimativa anterior é
repetida sem essa medição. A medição atual (`git diff --numstat f6aaded`) é:

| Categoria | Caminhos | Adições | Remoções | Classificação |
|---|---:|---:|---:|---|
| Manual (código, testes e docs) | 39 | 7.341 | 621 | Linhas revisáveis; inclui migrations `.cs`. |
| Designers de migration | 2 | 3.012 | 0 | Gerado pelo EF. |
| Snapshot EF | 1 | 150 | 0 | Gerado pelo EF. |
| Snapshot SQL | 1 | 86 | 0 | Gerado do schema. |
| Lockfiles | 3 | 368 | 6 | Restore gerado; sem dependência nova. |
| **Total** | **46** | **10.957** | **627** | **11.584** linhas alteradas. |

Logo, o total manual efetivo é 7.341 adições (6.720 líquidas), não a
estimativa anterior de aproximadamente 5.380. A exceção de tamanho aprovada
continua registrada; nenhuma linha toca `quality/private` e os três lockfiles
foram necessários ao restore travado.
