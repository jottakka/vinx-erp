# Módulo AI

## Estado

A base do módulo `Vinx.Erp.AI` entrega catálogo server-side de perfis
aprovados, resolução de contexto confiável fail-closed e um runner com
desfecho normalizado. A fatia [#135-A](https://github.com/jottakka/vinx-erp/issues/135)
adiciona somente o adapter do DeepSeek v4 Flash por endpoint OpenAI-compatible,
atrás de `IChatClient` (`Microsoft.Extensions.AI`) e do SDK oficial OpenAI 2.x.

O adapter serve leitura, resumo, extração e rascunho não persistido. Não existe
Anthropic, segundo provider, OpenCode, memória longa, vector store, streaming,
persistência de conversa, autorização, seleção de empresa, execução de tool,
API ou UI nesta fatia.

O único perfil aprovado continua sendo `read_only_summarize_v1` (journey
`summarize`, escopo de empresa, capability `ai.read`, `TurnLimit` 1,
orçamento de 8.000 tokens e `AllowedTools` vazio). A integração do provider
ao perfil e ao host fica para a API (#138), após o contrato estabilizar.

## Perfil server-side

`DeepSeekOptions` define o perfil aprovado e é validado no startup pelo options
pattern com `ValidateOnStart` (`DeepSeekOptions.Validator`). Endpoint, modelo,
timeout, orçamento e limites vêm sempre do perfil; o request do chamador nunca
escolhe esses valores. O adapter tira snapshot imutável na construção, então
alterar as options em runtime não troca endpoint, modelo ou limites de uma
instância viva.

Limites do perfil:

| Limite | Papel |
| --- | --- |
| `Timeout` (1s–5min) | Latência máxima por chamada, com cancelamento vinculado ao chamador |
| `MaxMessages` | Teto de mensagens por conversa normalizada |
| `MaxInputCharacters` | Teto determinístico de entrada antes de qualquer chamada HTTP |
| `MaxOutputTokens` | Teto de tokens de saída enviado ao provider |
| `MaxTotalTokensPerCall` | Orçamento por execução que cobre o limite de saída |

O uso normalizado reporta entrada, saída e total ao ledger do runtime; o
adapter não mantém estado entre chamadas.

## Fronteira e falhas

O request é sanitizado pelo mapper: somente os papéis
system/user/assistant/tool e conteúdos texto, imagem permitida (png/jpeg/webp,
somente bytes) e protocolo de tool seguem ao provider. `author`, propriedades
extras, JWT, e-mail, capability, segredo, documento bruto e prompt interno
são descartados antes da fronteira.

Tool proposals chegam tipadas (`FunctionCallContent` com argumentos parseados).
Argumentos inválidos viram `provider_invalid`, nunca resultado inventado. A
invocação é estruturalmente impossível: o adapter não monta
`FunctionInvokingChatClient`, mesmo com `ChatToolMode.Auto`.

Falhas externas são classificadas como `timeout`, `cancelled`, `rate_limited`,
`provider_invalid` ou `provider_unavailable`, sem retry, inclusive no SDK via
`ClientRetryPolicy(0)`. A exceção publicada tem mensagem fixa e redacted; body,
prompt, headers secretos e resposta bruta nunca aparecem em exceção ou log.
Cada execução recebe correlation ID devolvido na resposta e propagado no
escopo de logging. Cancelamento do chamador é `cancelled` e não gera log.

O SDK serializa o teto de saída como `max_completion_tokens`; o valor do
perfil é aplicado pelo adapter independentemente do campo aceito pelo endpoint
real.

## Ownership

O módulo é responsável por perfis aprovados, resolução de contexto confiável,
conversão do protocolo do provider, sanitização, classificação de falhas e
reporte de uso. Regras de negócio continuam nos módulos de domínio.
Identidade, autorização, capabilities, RLS e auditoria continuam na
Foundation. O registro DI e o `HttpClientFactory` pertencem à fatia da API
(#138).

## Company scope

O contexto segue ordem fail-closed: `company_required` →
`company_not_authorized` → `capability_denied` → resolvido. A empresa vem
exclusivamente do snapshot de autorização do ator criado pelo servidor.
Mensagem, canal, argumento ou claim não materializado nunca escolhem empresa.
`AgentExecutionContext` carrega somente identificadores e nomes normativos da
única empresa autorizada; nenhuma empresa fora do snapshot é consultada ou
exposta.

Não há requisição AI ativa exposta nesta fatia. A futura fronteira deve receber
ator e empresa de contexto confiável, reautorizar cada caso de uso na
Foundation e não compartilhar resposta entre atores ou empresas.

## Execução e desfechos

O `AgentRunner` segue ordem fixa: validação de entrada, resolução de contexto,
seleção de provider e, no máximo, uma chamada de modelo. Nenhuma falha
intermediária chama o modelo.

`AgentOutcome` cobre `Completed`, `NeedsClarification`, `RequiresConfirmation`,
`Denied`, `Invalid`, `Unavailable` e `TimedOut`, com reason codes estáveis:
`completed`, `message_empty`, `profile_unknown`, `company_required`,
`company_not_authorized`, `capability_denied`, `context_unavailable`,
`provider_not_configured`, `model_unavailable`, `model_timeout` e
`tool_proposal_requires_confirmation`.

Tool call volta como proposta que exige confirmação. Runner não executa tools,
não mantém loop de chat ou estado entre execuções, e nunca registra prompt,
resposta ou PII.

## Histórico e próximos passos

1. #110 entregou bootstrap HTTP para o frontend.
2. #132 entregou contexto confiável neutro de transporte.
3. #135-A integra o adapter DeepSeek; Anthropic e demais providers ficam fora.
4. #136 valida e redige a saída real.
5. #138 conecta contexto, provider, guardrails e aprovação numa API supervisionada.

A primeira opção continua sendo SDK oficial e `Microsoft.Extensions.AI`. Não
serão criados loop de chat, dispatcher de ferramentas, timers, fila,
checkpoint ou workflow próprios sem caso de uso hospedado e evidência de que a
plataforma não atende.

## Contract version

`3.0`

## Deletion strategy

O módulo não mantém dados para excluir; nenhuma conversa é persistida.
Aprovações duráveis continuam sob retenção, soft-delete e futura purga física
definidas pela Foundation.

## Migration notes

Não há migrações nesta fatia. A capability `ai.read` existe como constante
`AgentCapabilities.Read`; a seed no catálogo da Foundation fica para #138.
Sem seed, produção falha fechado (default deny). Tabelas, policies, grants e
capabilities de aprovação permanecem migrações da Foundation. A chave do
provider mora no cofre de configuração do host; nenhum endpoint ou segredo é
versionado neste documento ou no código de teste.
