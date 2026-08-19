# [Agente — provider OpenAI-compatible genérico + perfil de visão] Implementation Handoff

> **Para agentes:** esta é a fatia #135-B (segundo provider de #135). Usar
> GPT-5.6 Luna para testes de protocolo/fixtures; Sol revisa e aprova plano,
> endpoint, segredos, custo, redaction e contrato de visão; Terra revisa a
> documentação, valida findings e revisa o diff final. Revisão Terra do plano
> (2026-08-19): `CHANGES_REQUIRED` → 5 findings incorporados abaixo; re-review
> delta confirmou 1–4 resolvidos e o resíduo da estimativa corrigido.

**Goal:** entregar um adapter `IChatClient` genérico para qualquer endpoint
OpenAI-compatible aprovado, com modelo e limites vindos do perfil server-side
(configuration), cobrindo (a) providers de texto e (b) um perfil declarado de
visão (imagens) para consumo do OCR de #134.

**Architecture:** um único núcleo de adapter (`OpenAiCompatibleChatClient`)
sobre SDK oficial OpenAI 2.x + `Microsoft.Extensions.AI`, parametrizado por
`OpenAiCompatibleOptions`. O perfil declara endpoint, modelo, limites e
**modalidades aceitas** (`AllowImageInputs` + tetos de imagem); o mapper
rejeita deterministicamente conteúdo fora do perfil antes de qualquer HTTP.
DeepSeek vira um perfil nomeado do núcleo genérico, sem código de protocolo
duplicado (regra do projeto: repetição de plumbing em um lugar só).

## Fatos verificados (2026-08-19, docs oficiais DeepSeek + revisão Terra)

- A API hospedada (`api.deepseek.com`) expõe somente `deepseek-v4-flash` e
  `deepseek-v4-pro`, ambos **text-only**, contexto 1M, modos Thinking e
  Non-Thinking, compatíveis com OpenAI ChatCompletions e Anthropic.
- A rota ChatCompletions documenta `content` do user como string; blocos de
  imagem/documento aparecem como "Not Supported". Um array multimodal
  OpenAI-style com `image_url` é rejeitado (HTTP 400, request inválido).
- Consequência no código atual (confirmada por Terra, arquivo:linha): o
  mapper encaminha png/jpeg/webp (`DeepSeekRequestMapper.cs:70-72`), o SDK
  serializa como `image_url` com data URI (teste
  `DeepSeekChatClientContractTests.cs:100-114`) e a chamada inteira falha
  400 → `provider_invalid` (`DeepSeekResponseMapper.cs:118-127`). Falha
  segura, mas a allowlist de imagem do #135-A é caminho morto contra o
  endpoint hospedado.
- Famílias de visão/OCR do DeepSeek (VL2, Janus, DeepSeek-OCR/OCR-2) são
  open-weight: exigem self-host ou host terceiro OpenAI-compatible — nunca
  `api.deepseek.com`. É exatamente o caso de uso do provider genérico.

## Model routing

- **GPT-5.6 Luna:** mappers, contract tests, fixtures repetitivas.
- **GPT Sol:** plano, aprovação de endpoint/perfis, revisão crítica de
  secrets, timeout, custo, redaction, prompt injection e contrato de visão.
- **GPT Terra:** documentação não crítica, validação de findings, revisão do
  diff final (`codex exec -m gpt-5.6-terra -s read-only`).

## Exclusões obrigatórias

- Não implementar Anthropic, OpenCode, memória longa, vector store,
  streaming, persistência de conversa ou invocação de tool
  (`FunctionInvokingChatClient` proibido; `ToolMode.Auto` somente).
- Não enviar PDF ao provider: documento bruto é propriedade de #134
  (`DocumentTextExtractor`); visão recebe páginas rasterizadas, nunca o
  arquivo. Arquivos exclusivos de #134 não são tocados.
- Não aceitar endpoint/modelo do request: tudo vem do perfil aprovado.
- Não fazer retry (inclui `ClientRetryPolicy(0)` no SDK); falha externa é
  classificada, nunca convertida em sucesso.
- Não logar body, prompt, header, chave, imagem ou resposta bruta.
- Sem registro DI/`Program.cs`/`AddOptions().ValidateOnStart()` (fatia #138):
  o contrato desta fatia é validação pura na construção do perfil/client.

## Arquivos exclusivos

- Criar `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleChatClient.cs`
- Criar `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleOptions.cs`
- Criar `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/ProviderEgressPolicy.cs`
- Criar `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleRequestMapper.cs`
- Criar `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleResponseMapper.cs`
- Reduzir `src/Modules/Vinx.Erp.AI/Providers/DeepSeek/*` a perfis finos que
  delegam ao núcleo genérico, preservando o contrato público (decisão
  wrapper vs. remoção fica com Sol).
- Criar testes em `tests/public/Vinx.Erp.AI.UnitTests/Providers/OpenAiCompatible/`.
- Ajustar `tests/public/Vinx.Erp.AI.UnitTests/Providers/DeepSeek/` somente
  para a mudança intencional de contrato v2.2 (imagens, abaixo).
- Atualizar `docs/modules/ai.md` (modalidades, perfil genérico, contract
  version 2.2, mudança intencional) e criar
  `docs/verification/issue-135-b-openai-compatible-test-cases.md`.

Sem tocar API/UI/WhatsApp.

## Endpoint hardening (política pura; composição no host fica em #138)

Endpoint é configuration-driven. A validação roda na **construção** do
client (e é reusável pelo host em #138); `ProviderEgressPolicy` é uma
função pura testável:

1. URI absoluta `https` (sem http claro).
2. Porta: somente `443`, exceto porta explicitamente aprovada no perfil.
3. Host: hostname `localhost` e literais IP são rejeitados quando em faixa
   loopback, privada, reservada, link-local (inclui 169.254.169.254),
   unspecified, multicast ou documentação — cobrindo IPv4, IPv6 e
   IPv4-mapped IPv6 (ex.: `::ffff:10.0.0.1`), incluindo grafias numéricas
   incomuns (octal/hex) que resolvem para IP proibido.
4. Resolução DNS: todos os resultados A/AAAA são validados pela mesma
   regra. A política expõe validação por-endereço reutilizável para o
   `ConnectCallback` do `SocketsHttpHandler` em #138 (defesa contra DNS
   rebinding: validar **no momento da conexão**, não só na construção).
5. `ApprovedHosts` opcional no cofre de configuração: match por host exato
   normalizado (lowercase, sem trailing dot) ou sufixo com fronteira de
   ponto (`api.deepseek.com` aprova `eu.api.deepseek.com`, nunca
   `fakeapi.deepseek.com.evil.tld`); `EndsWith` ingênuo é proibido.
6. Requisitos registrados para #138 compor o transporte: redirects
   automáticos desabilitados (ou cada hop revalidado pela mesma política),
   proxy negado a menos que egress proxy aprovado exista, e
   `ConnectCallback` chamando a política por conexão.
7. A chave nunca aparece em mensagem de validação, log, exceção ou resposta.

## Contrato de visão (perfil declarado)

- `AllowImageInputs=true`: somente `DataContent` **por bytes** com media
  type png/jpeg/webp. Imagem por URL é rejeitada deterministicamente (o
  servidor nunca busca URL de request); media type fora da lista do perfil
  rejeita a conversa antes de HTTP (não é mais descartada em silêncio).
- Tetos do perfil: `MaxImagesPerRequest`, `MaxImageBytesPerImage` e
  `MaxImageBytesPerRequest` (agregado). Exceder qualquer teto = rejeição
  determinística antes de HTTP.
- Orçamento de admissão preflight documentado: estimativa por imagem (ex.:
  constante por imagem do perfil) conta contra `MaxTotalTokensPerCall`
  antes da chamada; usage real reportado após a execução, como hoje.
- Limite de dimensão/pixel decodado NÃO é inspecionado nesta fatia: abrir
  issue explícita de follow-up para inspeção segura de imagem (sem
  dependência nova sem aprovação — avaliar nativo/SDK antes).
- PDF permanece rota exclusiva de #134 (rasterização lá, imagens aqui).
- Perfis com `AllowImageInputs=false` (inclui DeepSeek hospedado): qualquer
  imagem na conversa = rejeição determinística antes de HTTP.

## Tasks

1. Escrever RED: perfil genérico (endpoint/modelo/limites), política de
   egress (matriz negativa: localhost, cada faixa de IP, IPv6/IPv4-mapped,
   porta não aprovada, sufixo de host malicioso, DNS resolvendo para IP
   privado), rejeição de imagem com `AllowImageInputs=false`, tetos de
   imagem quando `true`, resposta final, tool proposal, schema inválido,
   campos desconhecidos, timeout, cancelamento, sem retry.
2. Extrair o núcleo compartilhado (snapshot de perfil, timeout vinculado,
   correlation ID, classificação de falha, sanitização) para um lugar só;
   DeepSeek delega; demais testes DeepSeek seguem verdes sem edição.
3. **Mudança intencional de contrato v2.2** (aprovação Sol no plano):
   perfil DeepSeek passa a `AllowImageInputs=false`. O teste multimodal
   existente (`Allowed_multimodal_content_reaches_the_provider_as_image_url`)
   é SUBSTITUÍDO por "imagem é rejeitada antes de HTTP com call count zero";
   `docs/modules/ai.md` atualizado na mesma mudança, sem claim de
   `ValidateOnStart` até #138 existir.
4. Implementar validação de endpoint/egress como política pura na construção
   (ctor lança com mensagem fixa, sem ecoar chave/URI de segredo).
5. Declarar o perfil de visão (modalidades, tetos, estimativa de admissão)
   e abrir a issue de follow-up de inspeção de pixel.

## Estimativa

Contagem pelo padrão do repo (arquivos: todos exceto testes; linhas: todas
as manuais, inclusive testes):

- **Arquivos fora do cap de teste (11):** 5 novos em `Providers/OpenAiCompatible/`
  (ChatClient, Options, ProviderEgressPolicy, RequestMapper, ResponseMapper)
  + 4 ajustados em `Providers/DeepSeek/` (ChatClient, Options, RequestMapper,
  ResponseMapper — viram perfis finos) + 2 docs (`docs/modules/ai.md`,
  `docs/verification/issue-135-b-openai-compatible-test-cases.md`).
- **Testes (fora do cap de arquivos):** 7 novos/ajustados em
  `Providers/OpenAiCompatible/` + 2 ajustados em `Providers/DeepSeek/` +
  2 fixtures compartilhados extraídos para `Providers/`.
- **Linhas manuais (real, pós-implementação):** 21 arquivos,
  +2.705/−441 — **estouro justificado sobre o teto de 800**: a matriz
  negativa exigida (egress com ~35 casos de faixa/grafia/porta/allowlist,
  options, tetos de visão) e as substituições v2.2 multiplicam os testes
  (216 no total); o código de produção é ~950 linhas. Fatia mantida como
  vertical coesa com lista exclusiva de arquivos; aprovação explícita do
  responsável registrada no PR.

## BDD mínimo

- **Sucesso:** perfil genérico aponta para endpoint fake OpenAI-compatible;
  modelo do perfil viaja no request; resposta normalizada com correlation ID.
- **Visão:** perfil `AllowImageInputs=true` aceita imagem dentro dos tetos
  (bytes, contagem, agregado, admissão preflight); perfil `false` e imagem
  por URL rejeitam a conversa antes de HTTP com zero chamadas.
- **Acesso:** N/A — sem host/API registrado em #135-B (fronteira exposta e
  reautorização pertencem a #138); o análogo testado é "request nunca
  escolhe endpoint/modelo/porta: somente o perfil aprovado chega à wire".
- **Estado/exceções:** perfil inválido (egress/host/porta) falha na
  construção com mensagem fixa; timeout, cancelamento do chamador, 429, 400
  e 5xx mapeados nas classificações existentes; sem body/prompt/chave em
  exceção ou log; sem retry.
- **Validação/erro:** limite de mensagens/caracteres/imagens excedido falha
  antes de HTTP; conteúdo não permitido rejeitado, não descartado em
  silêncio.
- **Concorrência:** duas execuções com perfis nomeados distintos não
  compartilham client, modelo ou resposta.

## Verificação

`dotnet test tests/public/Vinx.Erp.AI.UnitTests` + ArchitectureTests +
`./scripts/verify-public.sh` EXIT:0 no HEAD exato, SERIALIZADO; Terra revisa
o diff final; Sol dá o freeze; comentário de evidência no PR e issue #135
atualizada.

## Execução isolada

Worktree descartável próprio (ex.: `.worktrees/issue-135b-openai-compatible`
ou `/private/tmp/vinx-135b-<suffix>`), branch
`codex/issue-135b-openai-compatible-provider` a partir de `origin/main`
fresco, prefixo `/tmp/vinx-135b-` para evidências. Não tocar
worktrees/branches/PRs de outros agentes nem o ledger
`HANDOFF-mvp-builder.md` (conflito esperado: `docs/modules/ai.md` e
`Directory.Packages.props`). Antes de gate pesado: `pgrep -fl dotnet;
docker stats --no-stream` — se houver run ativo de outro agente, aguardar.
