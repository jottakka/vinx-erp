# Módulo AI

O módulo AI hospeda o harness supervisionado do VINX ERP sobre o Microsoft
Agent Framework. Ele não possui regras de negócio nem armazenamento durável:
propostas, consumo, auditoria, autorização e idempotência pertencem à
Foundation.

## Ownership

- `VinxHarnessFactory` monta o agente real a partir do `IChatClient` injetado;
  os padrões não usados ficam desligados e a aprovação automática é
  fail-closed.
- `VinxToolPolicy` é a allowlist do servidor: nome, risco, capability e
  versão de política não são escolhidos pelo modelo.
- `VinxExecutionContext` recebe ator e empresa somente de código confiável do
  servidor; argumentos e texto do modelo nunca substituem esse contexto.
- `HarnessApprovalGate` valida a fronteira; `HarnessConfirmationCoordinator`
  coordena confirmação, veto, single-flight e retomada.
- O único feixe público do harness é `HarnessAgentBundle`, com exatamente
  `CreateSessionAsync` e `RunUserTurnAsync`. Agente cru, fonte de `RequestId` e
  resposta de aprovação ficam internos.

## Company scope

Toda proposta leva uma empresa explícita do servidor. A Foundation reavalia
membership ativa e `ai.writes.confirm` em escopo `company` antes de preparar,
confirmar ou vetar; RLS isola empresa e ator. O modelo não escolhe empresa,
ator, capability, política ou recurso.

## Ciclo de aprovação durável

O ciclo é `prepare → confirm` ou `prepare → veto`, com três `RequestId`
independentes:

- preparação: idempotência da criação; replay do mesmo payload devolve a
  mesma aprovação;
- confirmação: claim/replay do consumo; um vencedor recebe `Claimed`, o mesmo
  pedido recebe `Replay` e divergência recebe `Conflict`;
- execução: UUID criado pelo servidor e emparelhado ao feixe; a escrita do
  domínio pode devolver o resultado original em replay.

As seis fases citadas no código são: preparação durável; claim ou veto
durável; publicação local da tarefa terminal; entrega da decisão pelo loop do
framework; execução idempotente do domínio quando confirmada; retenção para
replay e remoção por TTL/sweep.

O claim e o veto são transações duráveis da Foundation, usam o relógio do
banco e repetem autorização em todo replay. Veto é soft-delete terminal com
`approval_vetoed`; consumo e veto são mutuamente exclusivos. Uma falha de
auditoria desfaz a transição.

Depois de `Claimed`, o coordenador marca a execução sob lock, publica a tarefa
antes de sinalizar os esperadores e inicia a retomada fora do lock. O
`VetoStart` usa a mesma barreira para que o veto não entregue uma resposta
enquanto o estado ainda está sendo publicado. Apenas o vencedor entrega uma
resposta de aprovação pelo gate; o coordenador nunca invoca ferramenta
manualmente: o turno continua pelo loop real do framework.

O feixe é opaco para o consumidor. `RunUserTurnAsync` aceita somente texto e
`CreateSessionAsync` cria a sessão; uma resposta de aprovação injetada como
turno de usuário falha fechada. O permit interno é vinculado a
`proposalId + CallId`, tem uso único e só é criado após claim durável. O gate
revalida contexto confiável, allowlist, decisão, proposta, `CallId` e permit
antes de qualquer entrega.

Entradas terminais permanecem no coordenador até a conclusão dos esperadores,
da execução/veto e do TTL. A varredura periódica usa o relógio do
`TimeProvider`, publica o cleanup antes de remover a entrada e executa a
limpeza de recursos depois do lock; a Foundation faz a expiração com o relógio
do banco antes de procurar conflito vivo. A purga física de linhas continua
fora desta fatia, na issue #201.

Replay com o mesmo `RequestId` pode adotar a propriedade após timeout entre o
commit e a observação local e reutiliza uma única conclusão estável. Outro
`RequestId` não pode adotar. Após reinício do processo, o registro em memória
não existe e a confirmação retorna `ProposalUnavailable`; não há promessa de
replay de `AgentResponse` através de reinicialização.

O shutdown fecha a porta antes de cancelar o trabalho, quiesce a varredura,
arbitra atomicamente o início das entregas de confirmação e veto, aguarda dreno
limitado e impede entrega tardia. Não é garantida execução
exatamente uma vez: aprovação é single-use e a escrita precisa de sua própria
idempotência de domínio.

Os delegates duráveis devem observar o token de cancelamento. O prazo do
coordenador sinaliza cancelamento, mas não tenta abandonar nem ocultar uma
operação de banco não cooperativa que ainda possa concluir.

## Contract version

`1.1`

## Deletion strategy

O módulo AI não apaga dados de negócio. Veto usa soft-delete e consumo é
terminal; retenção terminal em memória termina por TTL/sweep. A remoção física
das aprovações expiradas permanece na issue #201.

## Migration notes

O módulo AI não possui migrações. `foundation.approvals`, seus RLS, grants,
índices, constraints e a capability `ai.writes.confirm` são responsabilidade
das migrações da Foundation.

## Adiamentos

- Host HTTP, endpoints `/ai/approvals/*` e autenticação de host: N/A nesta
  fatia, vinculados à issue #138. A ausência de contexto é coberta como
  fail-closed; não se afirma uma fronteira HTTP inexistente.
- `GET /ai/whoami`: issue #125.
- Adaptadores de provedor chegam no PR 3; integração MCP, no PR 4.
- Hardening não bloqueante adiado pelo dono: matriz de gatilhos em ordem
  forçada/advisory locks; contagens exatas de descarte por recurso e
  instrumentação privada; reflexão ampla; agente sintético grande; todas as
  combinações exaustivas; stress, soak e repetição em grande escala. O pequeno
  ajuste de `VetoStart` permanece entregue.
