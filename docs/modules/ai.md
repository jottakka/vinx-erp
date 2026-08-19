# Módulo AI — adapter DeepSeek aprovado

## Estado

O módulo voltou a ter código na fatia [#135-A](https://github.com/jottakka/vinx-erp/issues/135):
somente o adapter do DeepSeek v4 Flash por endpoint OpenAI-compatible
aprovado, atrás de `IChatClient` (Microsoft.Extensions.AI) com SDK oficial
OpenAI 2.x. Não existe Anthropic, segundo provider, OpenCode, memória longa,
vector store, streaming nem persistência de conversa.

O adapter serve leitura, resumo, extração e rascunho não persistido. Ele
converte protocolo e devolve resposta normalizada ou proposta de tool; não
autoriza, não escolhe empresa, não invoca ferramenta e não persiste conversa.

## Perfil server-side

`DeepSeekOptions` define o perfil aprovado e é validado no startup pelo options
pattern com `ValidateOnStart` (`DeepSeekOptions.Validator`). Endpoint, modelo,
timeout, orçamento e limites vêm sempre do perfil; o request do chamador nunca
escolhe esses valores, e o adapter tira um snapshot imutável na construção:
mutar as options em runtime não troca endpoint, modelo ou limites de uma
instância viva.

Limites do perfil:

| Limite | Papel |
| --- | --- |
| `Timeout` (1s–5min) | Latência máxima por chamada, com cancelamento vinculado ao token do chamador |
| `MaxMessages` | Teto de mensagens por conversa normalizada |
| `MaxInputCharacters` | Teto determinístico de entrada antes de qualquer chamada HTTP |
| `MaxOutputTokens` | Teto de tokens de saída enviado ao provider (clamp; nunca excede o perfil) |
| `MaxTotalTokensPerCall` | Orçamento por execução: teto relacional que precisa cobrir o limite de saída |

O orçamento por execução é reportado pelo usage normalizado
(entrada/saída/total) para o ledger do runtime; o adapter não mantém estado
entre chamadas.

## Fronteira e falhas

O request é sanitizado pelo mapper: somente os papéis system/user/assistant/
tool e os conteúdos texto, imagem permitida (png/jpeg/webp, apenas bytes) e
protocolo de tool seguem para o provider. Author, additional properties e
qualquer conteúdo fora da allowlist são descartados antes da fronteira:
contexto confiável completo, JWT, e-mail, capability, segredo, documento bruto
e prompt interno não viajam no request.

Tool proposals chegam tipadas (`FunctionCallContent` com argumentos parseados);
argumentos inválidos viram falha `provider_invalid`, nunca resultado inventado.
A invocação de ferramenta é estruturalmente impossível: o adapter não monta
pipeline de invocação (não usa `FunctionInvokingChatClient`), mesmo com
`ChatToolMode.Auto`.

Falhas externas são classificadas em `timeout`, `cancelled`, `rate_limited`,
`provider_invalid` e `provider_unavailable`, sem retry (inclusive no SDK:
`ClientRetryPolicy(0)`). A exceção publicada tem mensagem fixa e redacted; body,
prompt, headers secretos e resposta bruta nunca aparecem em exceção ou log. O
cancelamento do chamador é `cancelled` e não gera log. Cada execução recebe um
correlation ID devolvido na resposta e propagado pelo escopo de logging.

O SDK aprovado serializa o teto de saída como `max_completion_tokens`; o valor
do perfil é aplicado pelo adapter independentemente do campo aceito pelo
endpoint real.

## Ownership

O módulo é responsável somente pela conversão de protocolo do provider
aprovado: mapeamento, sanitização de fronteira, classificação de falha e
reporte de uso. Regras de negócio continuam nos módulos; autorização,
identidade, RLS e auditoria continuam na Foundation. O registro DI e o
HttpClientFactory pertencem à fatia da API (#138), após o contrato estabilizar.

## Company scope

Não há requisição AI ativa exposta. A futura fronteira deve receber ator e
empresa de contexto confiável criado pelo servidor, nunca de texto, argumento
de tool ou claim não materializado, e deve reautorizar cada caso de uso na
Foundation. O adapter não compartilha resposta entre atores ou empresas.

## Ordem de retorno

1. #110 entregou bootstrap HTTP para o frontend;
2. #132 entrega contexto confiável neutro de transporte;
3. #135-A (esta fatia) integra o adapter DeepSeek por SDK oficial e `IChatClient`; #135 completa os demais adapters previstos;
4. #136 valida e redige a saída real;
5. #138 conecta contexto, provider, guardrails e aprovação numa API supervisionada.

A primeira opção continua sendo SDK oficial e `Microsoft.Extensions.AI`; não
serão criados loop de chat, dispatcher de ferramentas, timers, fila, checkpoint
ou workflow próprios sem caso de uso hospedado e evidência de que a plataforma
não atende.

## Contract version

`2.1`

## Deletion strategy

O módulo não mantém dados para excluir; nenhuma conversa é persistida. Aprovações duráveis continuam sob a retenção, soft-delete e futura purga física definidas pela Foundation.

## Migration notes

O módulo não possui migrações. As tabelas, policies, grants e capabilities de
aprovação permanecem migrações da Foundation. A chave do provider mora no cofre
de configuração do host; nenhum endpoint ou segredo é versionado neste
documento ou no código de teste.
