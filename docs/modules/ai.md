# Módulo AI — núcleo OpenAI-compatible e perfil DeepSeek

## Estado

O módulo tem o harness de contexto confiável (#132), o catálogo de perfis e
os providers (#135). Desde #135-B, o provider é um núcleo genérico
OpenAI-compatible (`OpenAiCompatibleChatClient`) atrás de `IChatClient`
(Microsoft.Extensions.AI) com SDK oficial OpenAI 2.x; o DeepSeek v4 Flash é
um perfil nomeado do núcleo, e perfis de visão podem ser declarados para o
OCR de #134. Desde #138 a API supervisionada `POST /agent/runs` expõe o
núcleo no ApiHost (ver seção API supervisionada). Não existe Anthropic,
OpenCode, memória longa, vector store, streaming nem persistência de
conversa.

O adapter serve leitura, resumo, extração e rascunho não persistido. Ele
converte protocolo e devolve resposta normalizada ou proposta de tool; não
autoriza, não escolhe empresa, não invoca ferramenta e não persiste conversa.

## Perfil server-side

`OpenAiCompatibleOptions` define o perfil aprovado: endpoint, modelo,
timeout, orçamento, limites, modalidades e allowlist de egress vêm sempre da
configuração; o request do chamador nunca escolhe esses valores. O client
tira um snapshot imutável na construção e valida o perfil na fronteira:
mutar as options em runtime não troca endpoint, modelo ou limites de uma
instância viva. Desde #138 o host registra o `Validator` com
`ValidateOnStart` na composição da API (`AddAgentRunSurface`).

Limites do perfil:

| Limite | Papel |
| --- | --- |
| `Timeout` (1s–5min) | Latência máxima por chamada, com cancelamento vinculado ao token do chamador |
| `MaxMessages` | Teto de mensagens por conversa normalizada |
| `MaxInputCharacters` | Teto determinístico de entrada antes de qualquer chamada HTTP |
| `MaxOutputTokens` | Teto de tokens de saída enviado ao provider (clamp; nunca excede o perfil) |
| `MaxTotalTokensPerCall` | Orçamento por execução: teto relacional que precisa cobrir o limite de saída |
| `MaxImagesPerRequest` / `MaxImageBytesPerImage` / `MaxImageBytesPerRequest` | Tetos de contagem e bytes (individual e agregado) quando o perfil aceita imagem |
| `EstimatedTokensPerImage` | Admissão preflight: estimativa por imagem conta contra o orçamento da chamada antes do HTTP |

O orçamento por execução é reportado pelo usage normalizado
(entrada/saída/total) para o ledger do runtime; o adapter não mantém estado
entre chamadas.

## Endpoint hardening (política de egress)

Endpoint é configuration-driven e validado por `ProviderEgressPolicy`
(função pura, sem I/O de rede) na construção do client: URI absoluta
`https`; porta 443 ou porta explicitamente aprovada; `localhost` e literais
IP em faixa loopback, privada, CGNAT, link-local (inclui 169.254.169.254),
reservada, multicast, documentação, unspecified ou IPv4-mapped são
rejeitados (IPv4, IPv6 e grafias equivalentes). `ApprovedHosts`, quando
presente no cofre de configuração, restringe por host exato normalizado ou
sufixo com fronteira de ponto — `EndsWith` ingênuo é proibido. A política
expõe validação por endereço resolvido para o transporte do host validar cada
conexão contra DNS rebinding; redirects automáticos e proxy também são
decisões do transporte. A composição de #138 registra o `HttpClientFactory`
nomeado com endpoint e timeout do perfil aprovado; a guarda por endereço
resolvido e o travamento de redirects permanecem pendentes de transporte
dedicado (registro na issue).

## Modalidades e contrato de visão

Perfis declaram o que aceitam. `AllowImageInputs=false` (padrão, inclui o
DeepSeek hospedado): qualquer conteúdo de imagem na conversa é rejeitado
deterministicamente antes de HTTP. `AllowImageInputs=true`: somente
`DataContent` por bytes com media type png/jpeg/webp, dentro dos tetos de
contagem e bytes e da admissão preflight. Imagem por URL nunca é aceita — o
servidor não busca URL de request; o ME.AI nem representa URL externa (o
construtor de `DataContent` exige data URI), e o mapper ainda rejeita
esquema não-data como defesa de fronteira. PDF nunca vai ao provider:
extração e rasterização pertencem ao #134 (`DocumentTextExtractor`), que
consume um perfil de visão com páginas em imagem. Limite de dimensão/pixel
decodado é follow-up registrado (inspeção segura sem dependência nova sem
aprovação).

## Fronteira e falhas

O request é sanitizado pelo mapper: somente os papéis system/user/assistant/
tool e os conteúdos texto, imagem permitida pelo perfil e protocolo de tool
seguem para o provider. Author, additional properties e qualquer conteúdo
fora da allowlist de modalidades são removidos ou rejeitados antes da
fronteira: contexto confiável completo, JWT, e-mail, capability, segredo,
documento bruto e prompt interno não viajam no request. Desde o contrato
2.2, conteúdo de dados fora do perfil (imagem em perfil text-only, media
type não aprovado, imagem por URL) é falha determinística antes de HTTP —
nunca descartado em silêncio e nunca erro 400 descoberto no provider.

Tool proposals chegam tipadas (`FunctionCallContent` com argumentos parseados);
argumentos inválidos viram falha `provider_invalid`, nunca resultado inventado.
A invocação de ferramenta é estruturalmente impossível: o adapter não monta
pipeline de invocação (não usa `FunctionInvokingChatClient`), mesmo com
`ChatToolMode.Auto`.

Falhas externas são classificadas em `timeout`, `cancelled`, `rate_limited`,
`provider_invalid` e `provider_unavailable`, sem retry (inclusive no SDK:
`ClientRetryPolicy(0)`). A exceção publicada tem mensagem fixa e redacted e
nenhuma causa bruta na cadeia pública (nem `Message`, nem `ToString()`, nem
`InnerException`): body, prompt, headers secretos e resposta bruta nunca
aparecem em exceção ou log. O cancelamento do chamador é `cancelled` e não
gera log. Cada execução recebe um correlation ID devolvido na resposta e
propagado pelo escopo de logging com o rótulo do perfil
(`ai/openai-compatible`, `ai/deepseek`).

O SDK aprovado serializa o teto de saída como `max_completion_tokens`; o valor
do perfil é aplicado pelo adapter independentemente do campo aceito pelo
endpoint real.

## Contexto de documento (OCR)

O pipeline `DocumentContext` transforma documento autorizado em contexto
normalizado: extração local da camada de texto (PdfPig) primeiro; raster + OCR
por visão (PDFtoImage/PDFium → perfil de visão) somente para páginas sem
texto; imagem direta é canonicalizada (decode → teto de pixels → strip de
metadados → re-encode) — o provider nunca recebe bytes originais. Saída é
JSON estrito com fonte/página/confiança e `untrusted: true`: texto de
documento é dado, nunca instrução. Allowlist de classificação/consentimento
(default deny) roda antes de qualquer raster/OCR; classe que exige redação é
rejeitada até #136.

A leitura é **efêmera**: nenhum contexto, cache ou resposta é retido entre
execuções; falhas (limite, timeout cooperativo, provider, schema) viram
`NeedsClarification` com razão fixa. Limites são compostos com o perfil de
visão vinculado: uma página por chamada e bytes codificados
`≤ min(teto OCR, MaxImageBytesPerImage)`, com degraus determinísticos de DPI
e rejeição segura. O PDFium não é thread-safe: rasterização é globalmente
serializada por gate; timeout de parse/render é cooperativo.

A fronteira de leitura é `IAuthorizedDocumentSource`, implementação owner da
Foundation (fatia #134-A): RLS, membership ativa, capability dedicada de
leitura/OCR, estado liberado, retenção e checksum, sem URL assinada. Sem a
fonte real — e sem engine de antimalware aprovado — o OCR em runtime é
fail-closed; os testes desta fatia usam fontes sintéticas controladas.

## Contrato de saída

`AgentOutputGate` valida a resposta normalizada do modelo
(`AgentModelResponse`) em ordem FIXA: contrato estrutural por jornada
(`AgentOutputContract`, campo obrigatório ausente ou desconhecido reprova,
JSON inválido falha fechado), autorização de fontes contra o contexto (fonte
de outra empresa nega; somente fontes da empresa do contexto passam) e
grounding/confiança (fontes exigidas sem autorizada, confiança abaixo do
piso 0,5 ou resposta sem texto e sem fontes abstém). Reason codes estáveis:
`ok`, `schema_invalid`, `cross_tenant_source`, `no_authorized_source`,
`low_confidence` e `no_evidence`. Negações e abstenções não expõem texto nem
fontes; a proposta de tool segue como estado puro (`HasToolProposal`) em
qualquer desfecho, sem caminho de execução.

O texto do caminho `ok` é o do modelo, sem transformação: o gate NÃO redige
conteúdo (CPF/CNPJ, banco, médico, links) nem detecta injeção de prompt por
lista de termos. Essas verificações não são confiáveis como regex/lista
fechada numa única linguagem — cobrem fração mínima do problema e geram
falso positivo em numeração operacional; quando um canal exigir redação de
PII ou triagem de injeção, será por detector não determinístico
(serviço/modelo) decidido por canal em #138+, nunca por padrão fixo aqui.

Toda decisão registra a política vigente `AgentPolicyVersion.Current`
(`2026-08-19.2`); mudança das regras do gate ou do piso de confiança exige
nova versão datada. A política é pura e stateless
por chamada. Desde #138 o runner compõe o gate como passo final e a API
supervisionada o aplica a cada resposta; nada aqui promete canal, provider ou
execução de ferramenta.

## API supervisionada (#138)

O ApiHost expõe `POST /agent/runs` (grupo `/agent` com autorização de sessão
interna e rate limit `foundation`): o corpo aceita SOMENTE
`{ requestId, journey, message, companyId? }` sob parse estrito (campos
fechados, tipos exatos, cap de 24 KiB por bytes efetivamente lidos) e o
cliente nunca envia provider, modelo, capability ou veredito. A ordem completa
é: autenticação → rate limit `foundation` → session gate interno → parse estrito (400 tipado `validation_error`
sem eco do payload) → idempotência por `requestId`/commandName `agent.run`
(replay devolve o mesmo corpo sem nova chamada; hash divergente vira 409
`idempotency_conflict` auditado) → validação de entrada → contexto confiável
(membership + `ai.read` escopo Company; empresa forjada nega sem consultar o
modelo) → gate de provider (fail-closed `provider_not_configured`) → UMA
chamada de modelo com seleção composta server-side → `AgentOutputGate` na
ordem fixa → resposta tipada `result | needs_clarification | denied |
validation_error | conflict | requires_confirmation` com correlation;
`requires_confirmation` é opaco (sem texto nem nome da ação). Timeout vira 504
`agent_timeout`; provider ausente/indisponível vira 503
`provider_not_configured`/`agent_unavailable`; desfechos 5xx não são
persistidos nem auditados e o `requestId` pode retentar. A persistência
(idempotência + auditoria `AuditSource.Ai` com metadata `{status,
reasonCode}`) acontece na mesma transação e nunca carrega prompt, resposta
bruta, transcript ou PII — o `ResultJson` guarda somente a resposta segura.
Execução local (sem key → 503 `provider_not_configured`; com key via env) no
runbook `docs/runbooks/local-development.md`; matriz BDD em
`docs/verification/issue-138-agent-api-test-cases.md`.

## Ownership

O módulo é responsável somente pela conversão de protocolo de providers
aprovados: mapeamento, sanitização de fronteira, classificação de falha e
reporte de uso. Regras de negócio continuam nos módulos; autorização,
identidade, RLS e auditoria continuam na Foundation. Desde #138 o registro DI
da superfície de execução (`AddAgentRunSurface`/`MapAgentRunSurface`), o
`HttpClientFactory` nomeado e a composição do transporte pertencem ao ApiHost;
o módulo permanece puro de host.

## Company scope

A única fronteira AI exposta é `POST /agent/runs` (#138): o ator e a empresa
de contexto confiável vêm do servidor (session gate + snapshot de autorização
da Foundation), nunca de texto, argumento de tool ou claim não materializado;
a jornada company-scoped exige `companyId` de empresa com membership e
capability `ai.read` em escopo Company, e cada resposta é isolada por
correlation. O adapter não compartilha resposta entre atores ou empresas.

## Ordem de retorno

1. #110 entregou bootstrap HTTP para o frontend;
2. #132 entrega contexto confiável neutro de transporte;
3. #135-A integrou o adapter DeepSeek por SDK oficial e `IChatClient`; #135-B
   (esta fatia) extraiu o núcleo OpenAI-compatible genérico, o perfil de
   visão e a política de egress; #135 completa os demais adapters previstos;
4. #134 normaliza PDF/imagem `clean` em contexto verificado, consumindo o
   perfil de visão;
5. #136 valida a saída real;
6. #138 (esta fatia) conecta contexto, provider, guardrails e aprovação na
   API supervisionada `POST /agent/runs`.

A primeira opção continua sendo SDK oficial e `Microsoft.Extensions.AI`; não
serão criados loop de chat, dispatcher de ferramentas, timers, fila, checkpoint
ou workflow próprios sem caso de uso hospedado e evidência de que a plataforma
não atende.

## Contract version

`2.4` — mudança intencional sobre a 2.3: adiciona a superfície HTTP
supervisionada `POST /agent/runs` (#138) — parse estrito do corpo
`{ requestId, journey, message, companyId? }`, idempotência por `requestId`
(commandName `agent.run`, `ResultJson` somente com a resposta segura),
auditoria `AuditSource.Ai` com metadata `{status, reasonCode}`, composição
server-side do provider (`AddAgentRunSurface`, fail-closed
`provider_not_configured`) e mapeamentos de fronteira (timeout 504
`agent_timeout`; indisponibilidade 503). A 2.3 permanece vigente no que
descreve: o contrato de saída validado (#136) com ordem fixa e versão de
política `AgentPolicyVersion` (`2026-08-19.2`), negações e abstenções sem
texto nem fontes, gate sem redação de conteúdo nem detecção de injeção
(detector não determinístico futuro por canal). A 2.2 permanece vigente no que descreve: a API hospedada do
DeepSeek é text-only, o perfil DeepSeek rejeita imagem antes de HTTP, e
conteúdo de dados fora do perfil é rejeitado deterministicamente em vez de
descartado em silêncio.

## Deletion strategy

O módulo não mantém dados para excluir; nenhuma conversa é persistida. Aprovações duráveis continuam sob a retenção, soft-delete e futura purga física definidas pela Foundation.

## Migration notes

O módulo não possui migrações. As tabelas, policies, grants e capabilities de
aprovação permanecem migrações da Foundation. A chave do provider mora no cofre
de configuração do host; nenhum endpoint ou segredo é versionado neste
documento ou no código de teste.
