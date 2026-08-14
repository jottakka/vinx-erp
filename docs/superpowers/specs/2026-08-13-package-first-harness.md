# Harness package-first do Agent Harness (issue #127, PR 1)

## Status

Aprovada em 13 de agosto de 2026. Esta decisão substitui o adiamento do
Microsoft Agent Framework definido em
`2026-08-11-agent-framework-boundaries-design.md` e entrega a primeira fatia
de produção: prova do harness com pacotes Microsoft reais.

## Conciliação

A instrução corretiva do responsável determina que o Microsoft Agent
Framework seja usado agora, em código de produção, e não adiado para depois
de V1. Ela sobrepõe o adiamento registrado na decisão de 11/08. As garantias
de segurança daquela decisão (ordem de política, confirmação pertencente ao
servidor, modelo sem poder de escolher empresa ou permissões) permanecem
válidas e são preservadas por esta fatia.

## Decisão package-first

O harness do VINX usa pacotes Microsoft estáveis em código de produção desde
a primeira fatia. É proibido recriar o loop modelo/ferramenta manualmente
(sem `AgentHarnessRunner` próprio, sem substituto de `IChatClient`, sem
referência de pacote sem uso em produção).

Pacotes fixados em `Directory.Packages.props` (verificados em nuget.org em
13/08/2026 e revalidados no restore):

| Pacote | Versão | Uso |
|---|---|---|
| `Microsoft.Extensions.AI` | 10.9.0 | Fixado centralmente; chega como dependência do harness. |
| `Microsoft.Extensions.AI.Abstractions` | 10.9.0 | Referência direta de `Vinx.Erp.AI`: `AIFunctionFactory`, `ApprovalRequiredAIFunction`, `ToolApprovalRequestContent`, `FunctionCallContent`, `IChatClient`, `ChatOptions`. |
| `Microsoft.Agents.AI` | 1.17.0 | Referência direta de `Vinx.Erp.AI`: `AIAgent`, `AgentSession`, `AgentResponse` (via `Microsoft.Agents.AI.Abstractions`, dependência direta deste pacote). |
| `Microsoft.Agents.AI.Harness` | 1.17.0 | Referência direta de `Vinx.Erp.AI`: `HarnessAgent`, `HarnessAgentOptions`, extensão `AsHarnessAgent`. |

`Microsoft.Extensions.Logging.Abstractions` (10.0.11) também foi fixado, pois
o gate de aprovação registra decisões com `ILogger`.

## Padrões do harness desligados

`VinxHarnessFactory` constrói o `HarnessAgent` a partir de um `IChatClient`
injetado (agnóstico de provedor) com:

- `DisableWebSearch = true`;
- `DisableToolAutoApproval = true` (aprovação fail-closed);
- `DisableFileMemory = true`;
- `DisableAgentSkillsProvider = true`;
- `DisableTodoProvider = true`;
- `DisableAgentModeProvider = true`;
- `DisableOpenTelemetry = true`;
- `MaximumIterationsPerRequest = 5`.

No `ChatOptions` do harness, `AllowMultipleToolCalls = false`: o gate processa
uma aprovação por resposta, e o framework documenta que uma chamada protegida
em um lote exigiria aprovação de todas as chamadas da resposta.

O binding de resposta de aprovação permanece habilitado
(`DisableApprovalResponseBinding` não é definido).

## Ferramentas de sonda (PR 1)

Duas ferramentas inertes, sem dados do ERP e não registradas em nenhum host:

- `GetHarnessProbeStatus`: leitura estática, criada via `AIFunctionFactory` e
  executada diretamente pelo framework;
- `RecordHarnessProbeWrite`: escrita com efeito apenas em um contador em
  memória, envolvida em `ApprovalRequiredAIFunction`; nunca executa sem
  confirmação do servidor.

O registro dirige o envolvimento: cada ferramenta passa pela classificação de
`VinxToolPolicy.TryClassify`; ferramentas de escrita são envolvidas em
`ApprovalRequiredAIFunction`, leituras executam diretamente e nomes fora da
allowlist falham no registro. Assim classificação e envolvimento não divergem.

## Fronteira de aprovação do servidor

`HarnessApprovalGate` consome `ToolApprovalRequestContent` exposto na resposta
do agente e decide somente com entrada do servidor:

1. contexto confiável (`VinxExecutionContext` com ator e empresa) é fornecido
   por código do servidor; valores equivalentes nos argumentos do modelo são
   ignorados;
2. allowlist por nome de ferramenta (`VinxToolPolicy`);
3. classificação de risco: somente escrita classificada como `Write` pode ser
   aprovada; solicitação de aprovação para leitura é tratada como erro de
   configuração e negada;
4. aprovação: `ApproveAsync` recebe a decisão do gate e revalida o próprio
   gate antes de executar (`PendingConfirmation`, solicitação presente,
   contexto válido e ferramenta `Write` na allowlist); qualquer outra
   combinação lança erro e nada executa. Só então emite
   `requestContent.CreateResponse(true)` e retoma com
   `agent.RunAsync(mensagem, sessão)` na mesma `AgentSession`;
5. veto: `CreateResponse(false)` devolvido na mesma sessão;
6. falta de contexto ou ferramenta fora da allowlist nega (fail-closed);
   resposta com mais de uma solicitação de aprovação pendente também é negada,
   sem expor solicitação para aprovação (nenhuma pendência é resolvida em
   silêncio; o turno não deve ser retomado).

A aprovação deve ser a interação imediatamente seguinte com a sessão: uma
resposta de aprovação sem correspondência com a solicitação pendente era
consumida como entrada comum e descartava a pendência (fail-closed, nunca
executa); o PR 2 cumpriu a promessa: esses no-ops silenciosos viraram erros
explícitos via `HarnessApprovalGate.ThrowIfUnmatchedApprovalResponse`
(spec `2026-08-14-vinx-policy-approval.md`).

Decisões são registradas com `ILogger`, template constante e propriedades
nomeadas; negações usam nível `Warning`. Sem payload, argumentos ou PII.

Contrato explícito: aprovações duráveis de uso único, auditoria e
idempotência pertenciam ao PR 2 — **cumpridas** na spec
`2026-08-14-vinx-policy-approval.md` (tabela `foundation.approvals`, serviços
de preparação/confirmação/veto, coordenador single-flight e escrita de sonda
idempotente por `RequestId` de execução). Nada nesta fatia prometia execução
exatamente-uma-vez, e o PR 2 mantém essa posição: aprovação single-use +
idempotência própria da execução.

## Limites do PR 1

- Somente ferramentas de sonda; nenhuma ferramenta de domínio.
- Ator/empresa reais, auditoria e idempotência chegam no PR 2 — **entregues**
  pelo PR 2 (spec `2026-08-14-vinx-policy-approval.md`); `VinxExecutionContext`
  passou a `Guid` com `Guid.Empty` inválido.
- Adaptadores de provedor (OpenAI-compatible, Anthropic) chegam no PR 3.
- Integração MCP chega no PR 4.
- O contrato de módulo `docs/modules/ai.md` ficava adiado até o PR 2 —
  **criado** no PR 2, quando o módulo passou a possuir contexto confiável e
  fronteira auditável.

## Validação local exigida

- `dotnet build` da solução;
- `dotnet test tests/public/Vinx.Erp.AI.UnitTests`;
- `dotnet format whitespace --verify-no-changes`;
- `dotnet restore --locked-mode` (lockfiles commitados).
