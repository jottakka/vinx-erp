# Módulo AI

O módulo AI possui o harness supervisionado do VINX ERP montado sobre o
Microsoft Agent Framework. Ele não possui regras de negócio de Paguai, Service
Desk, Kanban, HR ou Notificações, nem armazenamento durável próprio:
armazenamento, consumo, auditoria e idempotência de aprovações pertencem à
Foundation.

## Ownership

- O `HarnessAgent` e sua montagem (`VinxHarnessFactory`): padrões não usados
  do harness desligados, aprovação de ferramentas desativada por padrão
  (fail-closed) e limite de iterações por requisição.
- A allowlist, a versão de política, a classificação de risco e o mapeamento
  de capability das ferramentas (`VinxToolPolicy`) — todos pertencentes ao
  servidor.
- O contexto de execução confiável (`VinxExecutionContext`): ator e empresa
  fornecidos exclusivamente por código do servidor, nunca lidos de argumentos,
  texto ou JSON produzidos pelo modelo.
- A fronteira de aprovação (`HarnessApprovalGate`) e o ciclo de confirmação
  durável (`HarnessConfirmationCoordinator`): single-flight por proposta,
  retomada da sessão somente pelo vencedor e respostas de aprovação sem
  correspondência tratadas como erro explícito.
- As ferramentas de sonda inertes (`HarnessProbeTools`), usadas apenas como
  prova de fronteira; a sonda de escrita é idempotente por `RequestId` de
  execução.

## Company scope

Toda proposta de escrita carrega uma empresa explícita fornecida pelo
servidor. A autorização (membership ativa + capability `ai.writes.confirm` em
escopo `company`) é avaliada pela Foundation antes de qualquer validação
semântica de recursos; empresa e permissão nunca vêm do modelo ou do cliente.
O coordenador só deriva ator e empresa de contexto autenticado do servidor;
RLS na Foundation isola propostas por empresa **e** por ator.

## Ciclo de aprovação durável

O ciclo é `prepare → confirm` ou `prepare → veto`, com três escopos distintos
de `RequestId`:

- **Preparação**: controla a idempotência da criação; replay devolve a mesma
  aprovação.
- **Confirmação**: controla reivindicação/replay; somente o vencedor consome a
  proposta (relógio do banco no `UPDATE` condicional). Resultados: `Claimed`,
  `Replay`, `Conflict` e `ProposalUnavailable`.
- **Execução**: estável entre reinicializações; o serviço de domínio devolve o
  resultado original em replay. Nenhuma promessa exatamente-uma-vez: a
  aprovação é de uso único e a execução tem idempotência própria.

`HarnessConfirmationCoordinator` mantém agente, sessão e solicitação pendente
por `proposalId` em registro em memória. Somente a confirmação vencedora envia
`CreateResponse(true)` + `RunAsync`; replays com o mesmo `RequestId` de
confirmação aguardam e reutilizam o resultado do vencedor; a mesma sessão
nunca é retomada duas vezes e a resposta de aprovação é sempre a interação
imediatamente seguinte. Estado em processo é removido em consumo, expiração,
conflito e veto. Após reinício do processo, a proposta em memória é perdida e
a confirmação devolve `ProposalUnavailable` (sem promessa de replay de
resposta do agente).

## Contract version

`1.0`

## Deletion strategy

O módulo AI não mantém dados de negócio; nada aqui introduz política de
exclusão própria. Aprovações duráveis seguem a estratégia da Foundation
(soft-delete para veto, consumo como estado terminal); retenção e limpeza de
aprovações expiradas estão adiadas para a issue #201.

## Migration notes

O módulo AI não possui migrações próprias. A tabela `foundation.approvals` e a
capability `ai.writes.confirm` são migrações da Foundation consumidas por este
módulo via referência de projeto; nenhuma mudança em hosts nesta fatia.

## Adiamentos

- `GET /ai/whoami` e os endpoints supervisionados `/ai/approvals/*` ficam
  adiados até existir fluxo hospedado real; ver issues #125 e #138.
- Adaptadores de provedor (OpenAI-compatible, Anthropic) chegam no PR 3;
  integração MCP no PR 4.
