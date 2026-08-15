# Política e aprovação VINX (issue #127, PR 2)

> **Substituição parcial em 2026-08-15:** o desenho durável da Foundation
> permanece válido. Coordenador em memória, permit e retomada de sessão foram
> removidos. O estado vigente está em
> `docs/superpowers/specs/2026-08-15-backend-first-simplification.md`.

## Status

Foundation persiste e consome aprovações de uso único com autorização, RLS,
auditoria e idempotência. Não existe runtime AI e nenhum host expõe esse ciclo.
A primitiva permanece disponível para a futura API supervisionada #138.

## Decisões duráveis preservadas

1. Foundation é a autoridade de preparação, claim, replay, veto, expiração e
   auditoria.
2. Ator e empresa devem vir do contexto autenticado do servidor; valores do
   modelo ou cliente não concedem autoridade.
3. Preparação recebe empresa, `RequestId`, ação, payload e policy version.
   Confirmação recebe empresa, ApprovalId, ProposalId, `RequestId`, payload e
   policy version.
4. Cada prepare, confirm e veto reavalia membership, capability e RLS.
5. Segunda proposta viva do mesmo ator/empresa é `proposal_conflict` auditado.
6. Não existe promessa exatamente-uma-vez. Um futuro serviço de execução deve
   possuir idempotência própria.

## Garantias verificadas

Replay com o mesmo pedido só reutiliza resultado quando empresa, ator, payload,
policy version e identificadores coincidem. Foundation usa o relógio do
PostgreSQL para claim, veto e expiração; mutação e auditoria participam da mesma
transação e falha de auditoria desfaz a mudança.

| Escopo | Garantia |
|---|---|
| preparação | Mesmo payload reusa aprovação; payload divergente é `idempotency_conflict`. |
| confirmação | Um claim é `Claimed`; mesmo pedido é `Replay`; outro pedido após consumo é `Conflict`. |
| veto | Mesmo pedido é idempotente; veto após consumo é conflito. |
| execução | Fora desta fatia; terá chave e idempotência do serviço de domínio. |

## Foundation e schema

`foundation.approvals` mantém constraints para expiração, hash, veto, consumo e
terminalidade exclusiva. Índices únicos garantem proposta viva por ator/empresa,
`ProposalId`, `RequestId` de confirmação e idempotência do comando. O classifier
só converte nomes de constraint explicitamente pinados; qualquer outro erro
falha fechado.

Expiração, veto e consumo são terminais e mutuamente exclusivos. A linha fica
retida para replay e auditoria; purga física continua na issue #201.

## Runtime removido

Não há registro local, timer, sweep em memória, single-flight de sessão,
permit, entrega de decisão ou recuperação pós-crash em runtime AI. Esses
comportamentos não são alegados pelos testes atuais.

## Manifesto

Os cenários vigentes estão em
`docs/verification/issue-127-vinx-policy-test-cases.md`.
