# Política e aprovação VINX (issue #127, PR 2)

## Status

Aprovada em 14 de agosto de 2026 (revisão 2, após rodada dupla de revisão de
plano — Sol + subagente próprio). Esta decisão entrega a integração durável de
política/aprovação do harness: a Foundation passa a armazenar e consumir
aprovações de uso único, e o módulo AI orquestra a confirmação com
single-flight. Empilhada sobre a PR 1 (`2026-08-13-package-first-harness.md`).

## Decisões

1. **Coordenador executável.** `HarnessConfirmationCoordinator` possui o
   agente, a sessão e a solicitação pendente por `proposalId` opaco gerado na
   preparação durável. Sem ele, um endpoint consumiria a aprovação sem nunca
   executar o harness.
2. **Endpoints HTTP `/ai/approvals/*` adiados** até existir fluxo hospedado
   real (PR 3+). O ciclo prepare/confirm/execute desta fatia é exercido via
   serviços + coordenador em testes autorizados.
3. **`GET /ai/whoami` adiado** (issue #125): comportamento independente; o
   coordenador só deriva ator/empresa de contexto autenticado do servidor +
   empresa explícita, nunca do modelo/cliente.
4. **Contratos com empresa explícita.** prepare = empresa + `RequestId` de
   preparação + payload validado estruturalmente; confirm = empresa +
   `ApprovalId` + `ProposalId` + `RequestId` de confirmação + payload
   canônico. Ator sempre do contexto autenticado; capability, versão de
   política e risco nunca vêm de cliente/modelo.
5. **Fronteira.** `VinxToolPolicy` (ferramenta/risco/capability/versão) é a
   autoridade de política do módulo AI; a Foundation fornece armazenamento,
   consumo, auditoria e idempotência genéricos e nunca decide allowlist ou
   política AI. `Vinx.Erp.AI` referencia `Vinx.Erp.Foundation`; nenhum host
   muda nesta fatia.
6. **Sem promessa exatamente-uma-vez.** Aprovação é de uso único; a execução
   tem idempotência própria no serviço de domínio (`RequestId` de execução).
7. **Exceção de contagem de arquivos aprovada pelo dono** (>25 arquivos):
   registrada aqui e no corpo da PR; o inventário com contagens de linhas
   substitui qualquer estimativa vaga (seção final).
8. **Cinco classes de cenários BDD materializadas como testes xunit + este
   manifesto de verificação.** O projeto Reqnroll não é expandido nesta fatia;
   a forma Dado/Quando/Então aparece no manifesto
   (`docs/verification/issue-127-vinx-policy-test-cases.md`) apontando cada
   teste xunit.
9. **Política de substituição de proposta.** Uma segunda preparação do mesmo
   ator na mesma empresa enquanto existe proposta viva é negada
   (`proposal_conflict`) com negação auditada — fail-closed, nunca substituição
   silenciosa.

## Ordem canônica de autorização

Somente parsing estrutural do comando pode preceder a autorização; validação
semântica e resolução de recursos acontecem depois dela. Ordem: allowlist →
empresa explícita → membership/capability → risco/confirmação → validação
semântica → serviço (conforme `2026-08-11-agent-framework-boundaries-design.md`
§4).

## Três escopos de RequestId

| Escopo | Controla | Comportamento |
|---|---|---|
| **Preparação** | Idempotência da criação | Replay com mesmo payload devolve o mesmo `ApprovalId`; payload diferente → `idempotency_conflict`. |
| **Confirmação** | Reivindicação/replay do consumo | Vencedor grava o `RequestId` no claim; replay do mesmo `RequestId` devolve `Replay`; `RequestId` distinto após consumo → `Conflict` auditado. |
| **Execução** | Idempotência da escrita no domínio | Estável entre reinicializações e re-propostas; o serviço de domínio devolve o resultado original. |

Resultados distintos da confirmação: `Claimed`, `Replay`, `Conflict` e
`ProposalUnavailable`. Após reinício do processo, a proposta em memória do
coordenador é perdida: a confirmação devolve `ProposalUnavailable` e **não**
promete replay de `AgentResponse`.

## Fluxo em 6 fases e semântica pós-crash

1. **Preparação durável** (transação única): autorização (membership ativa +
   capability `ai.writes.confirm`, escopo `company`) → idempotência → INSERT
   da aprovação + auditoria. Crash antes do commit: nada persistido.
2. **Confirmação humana**: nova requisição autorizada com empresa,
   `ApprovalId`, `ProposalId`, `RequestId` de confirmação e payload canônico;
   a autorização é repetida inclusive em replay.
3. **Claim/consumo single-use**: `UPDATE` condicional único que compara
   `payload_hash` e `policy_version`, exige `consumed_at IS NULL` e
   `expires_at > now()` — ambos pelo relógio do banco — na mesma transação da
   auditoria. Crash após consumo e antes da retomada: aprovação consumida,
   nada executado; o cliente re-propõe (documentado; sem exatamente-uma-vez).
4. **Retomada do harness**: o coordenador valida o `proposalId` vivo; somente
   o vencedor envia `CreateResponse(true)` + `RunAsync` na mesma sessão.
   Proposta sem correspondência → erro explícito (promessa da spec de
   13/08 agora cumprida via `HarnessApprovalGate.ThrowIfUnmatchedApprovalResponse`).
5. **Execução idempotente**: serviço de domínio com `RequestId` de execução;
   replay devolve o resultado original.
6. **Falha de auditoria**: rollback total (`audit_unavailable`);
   indisponibilidade registra a métrica segura `approval_confirmation`.

## Matriz de concorrência/idempotência

- mesmo `RequestId` + mesmo payload → replay do resultado original;
- mesmo `RequestId` + payload diferente → `idempotency_conflict` redigido;
- aprovação consumida + novo `RequestId` → conflito redigido + negação auditada;
- dois confirms simultâneos, mesmo `RequestId` → um commit, um replay
  (8× mesmo `RequestId` em teste: 1 `Claimed` + 7 `Replay` + 1 auditoria);
- confirms simultâneos, `RequestId` diferentes → um sucesso, negações
  auditadas (4× distintos em teste: 1 `Claimed` + 3 conflitos auditados);
- expirada / policy version divergente / payload divergente / `proposal_id`
  divergente / id desconhecido → nenhuma mutação; erro redigido que não revela
  existência entre empresas;
- TTL e `consumed_at IS NULL` avaliados no mesmo `UPDATE` condicional; a
  `policy_version` vigente é revalidada na confirmação;
- empresa diferente ou ator diferente → invisível via RLS
  (`ProposalUnavailable`, sem negação auditada que revele o alvo).

## Single-flight (SOL-1)

O coordenador mantém por `proposalId`: agente, sessão, solicitação pendente,
payload, `RequestId` de execução e expiração. Somente a confirmação vencedora
(`Claimed`) envia `CreateResponse(true)` + `RunAsync`; replays concorrentes
com o mesmo `RequestId` de confirmação aguardam a conclusão do vencedor e
reutilizam o resultado; a mesma `AgentSession` nunca é retomada duas vezes
(flag `Resumed` sob lock + conclusão registrada antes da execução); a resposta
de aprovação é a interação imediatamente seguinte com a sessão, pois o
coordenador é o único interlocutor. Estado em processo é removido em consumo,
expiração, conflito e veto.

## Escrita de sonda com RequestId (SOL-3)

`HarnessProbeWriteCounter.RecordWrite(requestId)` recebe o `RequestId` de
execução fornecido pelo servidor na montagem do agente (nunca do modelo) e é
idempotente: replay com o mesmo `RequestId` devolve o resultado original sem
repetir a mutação. A execução permanece mediada pelo framework
(`ApprovalRequiredAIFunction`); o coordenador nunca invoca a ferramenta
manualmente. A auditoria de aprovação não substitui a auditoria da escrita,
que pertence ao domínio.

## Hash canônico de payload

Serialização canônica antes do hash: chaves ordenadas recursivamente por
comparação ordinal, números preservados pela representação bruta
(`GetRawText`), formatação invariante de cultura. Hash = SHA-256 em
hexadecimal minúsculo, 64 caracteres, validado por formato. A mesma regra é
usada na preparação e no re-hash da confirmação (teste dedicado de
ordenação de chaves entre prepare e confirm).

## Invariantes do schema `foundation.approvals`

- `id` + `created_at` obrigatórios; `CHECK (expires_at > created_at)` — sem
  verificação dependente de `now()` na definição;
- o claim usa o relógio do banco (`now()`) para consumo e expiração;
- `confirmation_request_id` anulável com índice único parcial
  (`WHERE confirmation_request_id IS NOT NULL`); o `request_id` herdado da
  auditoria registra a preparação;
- `proposal_id` único;
- `payload_hash` validado por formato (SHA-256 hex minúsculo, 64 caracteres);
- veto é estado terminal de bloqueio: soft-delete com motivo fixo
  `approval_vetoed` e check de completude; proposta consumida é terminal e não
  pode ser vetada;
- RLS por empresa **e** ator com `FORCE ROW LEVEL SECURITY`, fail-closed
  quando os contextos estão ausentes;
- grants do runtime: `SELECT`+`INSERT` em nível de tabela, `UPDATE` somente
  por coluna nas colunas de ciclo de vida; nenhum `DELETE`.

## Adiamentos com vínculo concreto

- `GET /ai/whoami` → issue #125 (associada na spec de 11/08).
- API/endpoints supervisionados `/ai/approvals/*` → issue #138 (associada na
  spec de 11/08).
- Retenção e limpeza de aprovações expiradas → issue #201 (criada nesta
  fatia; alvo e condição de reavaliação registrados lá).
- Cenário BDD "não autenticado": **adiado/N/A nesta fatia** — não existe
  fronteira de host/autenticação no módulo AI (sem endpoints); a barreira de
  autenticação pertence ao host (issue #138). A ausência de contexto é testada
  como fail-closed e as negações auditadas cobrem atores autenticados sem
  membership/capability e tentativas de outro ator/outra empresa.

## Inventário de arquivos e contagem de linhas (SOL-8)

Total: **41 arquivos** tocados/criados nesta fatia. Linhas geradas contadas
em separado (números exatos de `git diff f6aaded..HEAD`):

| Categoria | Arquivos | Linhas adicionadas |
|---|---|---|
| Foundation (manual) | 15 | 1.461 |
| AI (manual) | 7 | 474 |
| Testes (manual) | 8 | 1.195 |
| Docs (manual, português) | 5 | 427 |
| Gerados: designers de migração (2) | 2 | 3.002 |
| Gerados: model snapshot EF | 1 | 145 |
| Gerados: snapshot SQL do schema | 1 | 84 |
| Gerados: lockfiles (2) | 2 | 242 |

O total manual (3.557 linhas, testes inclusos) excede 800 linhas; a coesão é
justificada: o ciclo completo prepare→confirm/veto→retomada→execução é uma
unidade indivisível de segurança (qualquer fase isolada criaria uma fronteira
prometida sem a garantia correspondente), e as cinco classes de cenários
exigem a matriz completa de concorrência. Nenhuma linha toca `quality/private`;
nenhum host muda; nenhum pacote novo.
