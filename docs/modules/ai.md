# Módulo AI

O módulo AI mantém uma integração mínima com o Microsoft Agent Framework. Ele
prova o loop real do framework e a classificação fail-closed de ferramentas,
mas não hospeda execução de escrita nem possui regras ou dados do ERP.

## Ownership

- `VinxHarnessFactory` monta o agente a partir de um `IChatClient` injetado,
  desliga recursos não usados e mantém aprovação automática desabilitada.
- `HarnessAgentBundle` expõe somente criação de sessão e turno textual. O
  agente bruto e a entrada de respostas de aprovação não são públicos.
- `VinxToolPolicy` classifica as duas sondas inertes por nome e risco.
- `VinxExecutionContext` recebe ator e empresa de código confiável do servidor.
- `HarnessApprovalGate` apenas avalia propostas. Ele não confirma, veta, retoma
  sessão ou executa ferramenta.

## Company scope

O gate recebe ator e empresa somente por `VinxExecutionContext` criado no
servidor. As sondas não acessam dados empresariais. Uma futura ferramenta deve
revalidar membership, capability e RLS no serviço de aplicação a cada uso.

## Comportamento atual

A sonda de leitura executa pelo loop do framework. A sonda de escrita é
envolvida por `ApprovalRequiredAIFunction`; como `DisableToolAutoApproval`
permanece ativo e não existe rota pública de entrega, ela termina como
`ToolApprovalRequestContent` sem efeito colateral.

O gate nega `CallId` vazio, contexto ausente ou inválido, ferramenta fora da
allowlist, solicitação de aprovação para leitura e mais de uma solicitação na
mesma resposta. Uma escrita válida recebe somente a classificação
`PendingConfirmation`; isso não concede poder de execução.

Nenhum host registra as sondas. Elas não acessam dados do ERP, não recebem
segredos e servem somente como prova pública da integração package-first.

## Relação com a Foundation

A Foundation contém a primitiva durável de `prepare/confirm/veto`, com RLS,
autorização, auditoria e idempotência. O módulo AI não referencia a Foundation
e ainda não existe costura entre essa primitiva e uma sessão de agente.

Quando a API supervisionada for implementada na issue #138, o transporte deve
chamar serviços de aplicação do backend com ator e empresa derivados da sessão
autenticada. A decisão sobre retomada, checkpoint e workflow será tomada a
partir desse primeiro fluxo real; não se reintroduz coordenação em memória por
antecipação.

## Contract version

`1.2`

## Deletion strategy

O módulo não persiste dados. Sessões do harness são efêmeras e esta fatia não
mantém fila, timer, cache de aprovação ou estado terminal para limpar.

## Migration notes

O módulo não possui migrações nem depende do projeto Foundation. Migrações de
aprovação durável e a capability `ai.writes.confirm` pertencem à Foundation.

## Adiamentos

- baseline ASP.NET Core, bootstrap autenticado e contexto confiável precedem a
  API supervisionada;
- MCP #133 reutiliza os mesmos serviços de aplicação, sem regra duplicada;
- adaptadores de provider #135 usam SDK oficial e `IChatClient`;
- endpoints de aprovação e execução de escrita permanecem em #138;
- purga física de aprovações expiradas permanece em #201.
