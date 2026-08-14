# Decisão de frameworks do Agent Harness

## Status

Aprovada para planejamento em 11 de agosto de 2026. Esta decisão não adiciona
pacotes nem altera comportamento de produção.

**Atualização de 13/08/2026:** o adiamento do Microsoft Agent Framework e a
proibição de `FunctionInvokingChatClient`/invocação de ferramentas pelo
framework foram **superados** por
`2026-08-13-package-first-harness.md`, por instrução corretiva do responsável.
O harness do Microsoft Agent Framework agora executa o loop de ferramentas em
produção, com aprovação fail-closed controlada pelo VINX. Os invariantes de
segurança desta decisão continuam válidos e foram preservados: a ordem de
política da seção 4, a confirmação como decisão do servidor (nunca do modelo)
e a proibição de o modelo escolher empresa, provedor ou capability.

## Objetivo

Entregar um agente supervisionado que possa servir a UI, WhatsApp e clientes
MCP sem duplicar protocolos de provedores ou de MCP, mas sem delegar ao modelo
ou a um framework decisões de autorização, empresa, aprovação, auditoria ou
idempotência.

O foco de V1 é ajudar em trabalhos concretos do ERP fora da UI: consultar,
resumir, redigir, extrair conteúdo de documentos e preparar comandos
autorizados. O WhatsApp faz parte de V1. Escritas de risco continuam
supervisionadas por confirmação durável.

## Contexto e limites

O ERP é um monólito modular .NET. A API, o futuro `Mcp.Host` e os workers
chamam os mesmos serviços de aplicação. A empresa é explícita para comandos,
e a autorização, auditoria e idempotência pertencem a Foundation. Os contratos
MCP existentes proíbem CRUD, SQL e inferência de empresa.

Um framework deve reduzir código de protocolo: conteúdo multimodal, streaming,
tool calls, serialização de provedores e transporte MCP. Ele não pode ser a
fonte de verdade de contexto confiável, de política de ferramentas ou de uma
aprovação de negócio. Esses requisitos são específicos do VINX e devem ter
testes determinísticos independentes do modelo.

## Decisão

### 1. Adotar `Microsoft.Extensions.AI` na fronteira de modelos

> **Superado em 13/08/2026** por `2026-08-13-package-first-harness.md`: o
> harness usa o loop de ferramentas do Microsoft Agent Framework (que envolve
> invocação de função gerenciada pelo framework), com aprovação fail-closed
> mantida pelo gate VINX. A adoção de `IChatClient` como fronteira comum
> permanece válida.

Os adaptadores de provedor usarão `Microsoft.Extensions.AI.Abstractions` e
`IChatClient` como fronteira comum para mensagens, conteúdo multimodal,
streaming e propostas de tool call.

O harness consome propostas de tool call manualmente. Ele não usará
`FunctionInvokingChatClient`, `UseFunctionInvocation()` ou outro recurso que
invoque uma ferramenta automaticamente. Uma resposta do modelo nunca é uma
autorização para executar uma ação. *(Trecho superado em 13/08/2026; a
invariante "resposta do modelo não é autorização" permanece válida.)*

O `IChatClient` substitui serialização repetida de turnos e conteúdo de modelo;
não substitui a política do agente. O núcleo VINX permanece pequeno: recebe o
contexto confiável, limita a lista de ferramentas, aplica a política e chama
um serviço de aplicação apenas depois das verificações necessárias.

### 2. Usar adaptadores estreitos por protocolo de provedor

Há dois adaptadores de produção em V1:

- **OpenAI-compatible:** um adaptador `IChatClient` para APIs compatíveis com
  OpenAI. Endpoint, modelo, limites e retenção são selecionados somente por um
  perfil de tarefa aprovado no servidor; não chegam da UI, WhatsApp ou MCP.
- **Anthropic Messages:** um adaptador estreito sobre o SDK C# oficial da
  Anthropic. Ele normaliza blocos `text`, imagem/documento e `tool_use` para o
  mesmo turno interno usado pelo harness.

Os adaptadores podem converter somente o protocolo. Não recebem ator confiável,
capabilities, confirmação, identificadores de empresa não autorizados ou
acesso direto a serviços de domínio. Um provedor novo exige um novo adaptador
com testes de contrato; não exige mudar ferramentas ou regras de negócio.

Não será adicionado pacote nesta PR de documentação. A primeira PR que usar
cada biblioteca fixa sua versão em `Directory.Packages.props`, usa o pacote em
código de produção e inclui testes do adaptador. Não haverá dependência sem uso
atual.

### 3. Usar SDK C# oficial de MCP somente no futuro `Mcp.Host`

O `Mcp.Host` futuro usará o SDK C# do Model Context Protocol para JSON-RPC,
descoberta e transporte. Para acesso remoto novo, o padrão é Streamable HTTP
estateless, com OAuth 2.1 na borda. SSE legado só pode existir como
compatibilidade explícita, com limitação e teste próprios.

O SDK implementa o fio MCP; ele não determina quem é o ator, quais empresas
ele pode acessar, qual ferramenta aparece no catálogo ou se um comando pode
ser confirmado. Os handlers MCP continuam finos e chamam os mesmos serviços de
aplicação da API.

O catálogo será compatível com Claude e GPT porque usa contrato MCP padrão,
não porque o modelo recebe acesso amplo. `whoAmI` retorna a visão permitida do
ator autenticado, suas empresas ativas e capacidades efetivas; nunca aceita um
identificador de pessoa fornecido pelo cliente.

### 4. Manter autoridade de segurança dentro do VINX

> Esta seção permanece válida e é preservada pela implementação
> package-first de 13/08/2026.

O fluxo obrigatório para cada proposta de ferramenta é:

```text
Host autenticado
  -> resolvedor de contexto confiável e perfil de tarefa
  -> modelo recebe somente contexto limitado e descritores permitidos
  -> modelo devolve texto ou proposta de ferramenta
  -> allowlist do perfil
  -> empresa explícita
  -> associação/capability do ator
  -> risco e estado de confirmação
  -> validação semântica da ferramenta
  -> serviço de aplicação
  -> auditoria, idempotência e resultado redigido
```

O parsing mínimo necessário para identificar descritor e empresa não é uma
autorização. Validação de schema e de regras de domínio acontece depois da
política acima. Erros devolvidos ao modelo e ao canal são redigidos; nunca
incluem dados de outra empresa, segredos, prompts ou conteúdo de arquivo.

Para escrita financeira, externa, irreversível ou outro risco definido pela
política, a proposta cria ou reutiliza uma aprovação Foundation. A aprovação é
de uso único, curta, ligada a ator, empresa, ação, hash do payload e versão da
política. Confirmar é uma requisição nova e autorizada; não é uma continuação
automática da conversa do modelo.

```text
Modelo propõe escrita
  -> política VINX decide que confirmação é necessária
  -> Foundation persiste aprovação e evidência de auditoria
  -> humano confirma com contexto autenticado
  -> serviço de aplicação executa de forma idempotente
```

### 5. Não adotar um runtime genérico de agentes em V1

| Opção | Decisão | Motivo |
| --- | --- | --- |
| `Microsoft.Extensions.AI` | Adotar | Padroniza a fronteira .NET de modelos sem tomar posse de autorização ou execução. |
| SDK C# de MCP | Adotar quando `Mcp.Host` começar | Evita implementar JSON-RPC e Streamable HTTP. |
| Semantic Kernel | Não usar em V1 | Auto-invocação é seu caminho padrão; mesmo no modo manual, acrescentaria abstração sobreposta ao loop supervisionado necessário. |
| Microsoft Agent Framework | Adiar *(superado em 13/08/2026: adotado no PR 1 da issue #127, com gate de aprovação VINX fail-closed; ver `2026-08-13-package-first-harness.md`)* | Oferece harness, sessões e workflows úteis, mas V1 precisa provar que sua integração preserva a ordem de política VINX e a aprovação durável antes de terceirizar o loop. |
| OpenAI Agents SDK | Não usar em V1 | O runtime é Python/TypeScript e toma posse de turnos, ferramentas, guardrails e sessões; adicionaria outro runtime ao monólito .NET. |
| LangGraph | Não usar em V1 | É runtime Python/JS para estado, persistência e humano-no-loop, responsabilidades que Foundation precisa controlar para comandos do ERP. |

Microsoft Agent Framework pode ser reavaliado depois de V1 se houver uma versão
estável adequada, suporte comprovado aos dois protocolos de provedor, teste de
integração que prove a política antes da validação de domínio e integração com
a aprovação Foundation sem duplicar estado ou auditoria.
*(Superado em 13/08/2026: a reavaliação ocorreu antes de V1 e resultou na
adoção package-first; os critérios de prova de política e de integração com a
aprovação Foundation continuam como condição para os próximos PRs.)*

## Escopo de V1

V1 entrega um agente supervisionado, não autônomo:

- UI e WhatsApp entram como canais do mesmo contrato de entrada e saída.
- Um resolvedor constrói contexto confiável de ator, empresas, memberships,
  capabilities e perfil de tarefa.
- Adaptadores OpenAI-compatible e Anthropic expõem conteúdo de texto e
  multimodal sob `IChatClient`.
- Ferramentas são jobs com `empresa_id` explícito, incluindo `whoAmI` para o
  ator autenticado e escritas somente com aprovação quando a política exigir.
- `Mcp.Host` remoto fornece catálogo interoperável para Claude e GPT depois que
  o contexto confiável e a confirmação Foundation existirem.
- OCR normaliza PDF/imagem privada e já aprovada pelo pipeline de arquivos em
  dados estruturados validados; a extração não toma decisão de negócio nem
  aprova documento.
- Guardrails de saída validam forma, abstêm em incerteza ou falta de acesso e
  removem vazamento de dados antes da resposta do canal.

V1 não usa memória persistente de conversa do provedor, vector store, arquivos
persistidos no provedor, subagentes, handoffs ou execução autônoma de escrita.
Também não envia prompts, conteúdo médico, documentos, credenciais ou respostas
brutas para logs, auditoria ou telemetria.

## Escopo de V2

V2 só começa após dados de uso e avaliação de V1. Itens candidatos são:

- workflows longos e retomáveis que tenham necessidade comprovada de runtime
  adicional;
- nova avaliação de Microsoft Agent Framework para um caso isolado *(item
  sem objeto desde 13/08/2026: o framework foi avaliado e adotado na
  sequência package-first; ver `2026-08-13-package-first-harness.md`)*;
- memória explícita e governada fora do provedor, com retenção, exclusão e
  autorização próprias;
- mais ferramentas de domínio, OCR especializado e revisão humana específica;
- automações adicionais de canal, sem remover a confirmação de ações de risco.

Nenhum item de V2 reduz as garantias de empresa explícita, capability,
aprovação, auditoria ou idempotência.

## Sequência de implementação condicionada

> O bloqueio abaixo foi **superado em 13/08/2026**: a issue #127 foi
> replanejada como a sequência package-first (PR 1: prova do harness com
> pacotes Microsoft; PR 2: política/aprovação; PR 3: provedores; PR 4: MCP).

As PRs de harness em rascunho permanecem bloqueadas até serem replanejadas
contra esta decisão. A implementação começa por uma fatia estreita de núcleo e
política, sem loop específico de provedor duplicado. A sequência esperada é:

1. Replanejar a issue #127 como núcleo portátil de contexto, política e
   contrato; não mesclar a stack atual antes dessa revisão.
2. Entregar o resolvedor de contexto e perfil confiável (#132).
3. Entregar os adaptadores OpenAI-compatible e Anthropic em PRs independentes
   (#135-A e #135-B), cada um com testes de contrato.
4. Entregar guardrails determinísticos de saída (#136-A e #136-B) e aprovação
   durável Foundation (#126).
5. Construir transporte MCP/OAuth separadamente (#133-A e #133-B), `whoAmI`
   (#125) e conformance de catálogo para clientes externos (#139).
6. Integrar API supervisionada, OCR e WhatsApp em fatias próprias (#138, #134
   e #140), sempre usando o mesmo núcleo.

O plano detalhado definirá dependências, BDD, estimativa e fronteira de cada PR.
Nenhuma fatia deverá combinar banco, provedor externo, transporte e CI sem uma
dependência indivisível.

## Critérios de aceite arquiteturais

- ~~Nenhuma tool call é automaticamente executada por biblioteca, adaptador ou
  provedor.~~ *(Superado em 13/08/2026: o harness do Microsoft Agent Framework
  executa leituras autorizadas diretamente. Critério vigente: nenhuma escrita
  executa sem confirmação explícita do servidor, e uma resposta do modelo
  nunca é uma autorização.)*
- Todo comando passa por contexto confiável, empresa explícita, capability e
  política de risco antes de sua validação semântica e do serviço de aplicação.
- Toda escrita tem auditoria e idempotência; as de risco têm confirmação
  Foundation durável e ligada ao payload.
- O modelo escolhe apenas entre descritores já autorizados pelo perfil; ele não
  seleciona provedor, endpoint, modelo, empresa ou capability.
- MCP, API, UI e WhatsApp compartilham serviços de aplicação, nunca uma regra
  de negócio exclusiva de transporte.
- Logs, métricas, auditoria e respostas ao modelo usam somente metadados
  redigidos e seguros.
- Cada adaptador, ferramenta e fluxo de confirmação tem testes públicos de
  sucesso, validação, acesso, estado e concorrência quando aplicável.

## Referências consultadas

- [Microsoft.Extensions.AI: `IChatClient`](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient)
- [SDK C# do MCP: transportes](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md)
- [SDK C# oficial da Anthropic](https://github.com/anthropics/anthropic-sdk-csharp)
- [Semantic Kernel: function calling](https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/function-calling/)
- [Microsoft Agent Framework: agentes .NET](https://learn.microsoft.com/en-us/dotnet/ai/conceptual/agents)
- [OpenAI Agents SDK](https://openai.github.io/openai-agents-python/)
- [LangGraph overview](https://docs.langchain.com/oss/python/langgraph/overview)
