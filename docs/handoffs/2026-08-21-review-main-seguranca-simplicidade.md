# Handoff — review da `main`: segurança, simplicidade e código manual

## Contexto

Review somente leitura da `main` (commit `7f6d745`, igual a `origin/main`), feito em
worktree isolado em `/Users/fjll/.codex/worktrees/756e/vinx-erp`. Nenhum arquivo de
produção foi alterado. Escopo proibido (`quality/private`) não foi lido.

Método: código dividido em 5 subsistemas revisados em paralelo, com foco em
segurança, simplicidade e substituição de código manual por framework/biblioteca
já presente na stack (.NET 10, EF Core 10, Wolverine, Microsoft.Extensions.AI +
SDK OpenAI, MCP oficial, OpenTelemetry, Supabase; FE React 19 + MUI + TanStack
Query + zod + react-hook-form).

Totais: 116 achados — 5 bug, 17 risco, 60 nit, 34 questão. Nenhum segredo
commitado, nenhuma injeção de SQL, nenhum sink de XSS encontrado.

## Prioridade imediata

1. **Ingesta WhatsApp perde imagem admitida** —
   `src/Hosts/Vinx.Erp.ApiHost/Notifications/InboundWhatsAppIngressService.cs:191`.
   Falha do flush do outbox vira Warning após o commit da admissão; a redelivery
   da Meta cai no dedup (`replay`) e `InboundWhatsAppImageReceived` nunca é
   publicado. Falhar a requisição antes do commit ou persistir marcador
   pendente com sweeper de republicação.
2. **Envio confirmado gravado como rejeitado; falha de rede encalha entrega** —
   `src/Modules/Vinx.Erp.Notifications/Providers/Meta/MetaWhatsAppClient.cs:92`
   classifica resposta com `messages` sem `id` legível como `Rejected`, enquanto
   `SendAsync` (:208) classifica a mesma forma como `Delivered`; :110 não mapeia
   `HttpRequestException` como transitória, soltando a lease só em
   `NotificationProviderTransientException` — entrega fica permanentemente em
   `provider_in_flight` (skip-list em `NotificationDeliveryWorkerService.cs:197`)
   e linha inbound fica presa em `processing`. Alinhar classificação e mapear
   exceção de transporte como na `MetaWhatsAppMediaClient.cs:131`.
3. **Sign-out trata indisponibilidade como sucesso** —
   `apps/web/src/features/auth/session-provider.tsx:77`. 503
   `identity_gate_unavailable` é tratado como logout concluído; o cookie BFF
   segue válido no servidor. Falhar fechado ou avisar que a sessão pode seguir
   ativa.
4. **Webhook anônimo sem limite de corpo durante cópia e sem rate limit** —
   `src/Hosts/Vinx.Erp.ApiHost/Notifications/MetaWhatsAppWebhookEndpoints.cs:73,25`.
   O corpo é todo bufferizado (~30 MB default do Kestrel) antes do teto de 24 KB,
   e o grupo anônimo não tem política de rate limit. Limitar durante a cópia
   (`IHttpMaxRequestBodySizeFeature` ou leitura incremental) e aplicar política
   por IP como a de login.
5. **Política de egress AI sem validação de endereço resolvido** —
   `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/ProviderEgressPolicy.cs:134`.
   `ValidateResolvedAddresses` nunca é invocada por transporte algum; DNS
   rebinding leva host aprovado a metadata/loopback. Registrar
   `SocketsHttpHandler.ConnectCallback` chamando a validação por conexão.

## Por subsistema

### Hosts (ApiHost, WorkerHost, McpHost) — 24 achados

- `src/Hosts/Vinx.Erp.McpHost/Program.cs:21` — risco: todos os clientes
  autenticados dividem um único bucket de 60 req/min; um tenant esgota o MCP
  para todos. Particionar por claim `sub`, como em `ApiHost/Program.cs:274-279`.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:279` — risco: bucket `"anonymous"`
  global compartilhado gera 429 em lockstep entre usuários distintos; particionar
  por IP remoto.
- `src/Hosts/Vinx.Erp.WorkerHost/WorkerDeadLetterRedactionInterceptor.cs:58` —
  risco: mensagem desconhecida é reescrita como
  `OwnerInvitationDeliveryRequested(Guid.Empty)`, envenenando inspeção/replay da
  DLQ. Falhar fechado ou guardar bytes neutros de tipo.
- `src/Hosts/Vinx.Erp.WorkerHost/WorkerDeadLetterRedactionInterceptor.cs:24` —
  risco: interceptor global sobrescreve a exceção original por uma genérica,
  descartando a causa raiz em toda escrita de DLQ. Redigir só payload/headers.
- `src/Hosts/Vinx.Erp.WorkerHost/Program.cs:221` e
  `src/Hosts/Vinx.Erp.McpHost/Program.cs:13` — questão: `AddOpenTelemetry()` sem
  `WithTracing`/`WithMetrics`/exporter; coleta nada. Cabear fontes
  (Wolverine/Npgsql/AspNetCore) ou remover.
- `src/Hosts/Vinx.Erp.WorkerHost/Program.cs:107` — questão:
  `NpgsqlDataSource.Create` roda antes de `ValidateOnStart`; connection string
  ausente estoura erro cru do Npgsql e datasource é criado mesmo nos modos
  `db-apply`/`db-assert`. Criar lazily via `IOptions`.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:254` — questão: throttle de login
  reimplementado lendo `PolicyName` do metadata do endpoint; segunda camada
  acoplada a constante de string. Expressar no composition da própria policy.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:757` — questão: Program.cs de 849 linhas
  carrega dois parsers JSON manuais de contrato HTTP
  (`PlatformOrganizationHttpContract`, `EmployeeDirectoryHttpContract`) além de
  lambdas inline, duplicando a infra de binding estrito que já existe
  (`JsonUnmappedMemberHandling.Disallow` em :184, `StrictJsonBodyAttribute`).
- `src/Hosts/Vinx.Erp.ApiHost/Identity/BrowserBffRequestMiddlewares.cs:21` —
  questão: lista hardcoded de rotas para `no-store`; dirigir por metadata do
  endpoint.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:333` — questão: sem
  `UseHttpsRedirection`/HSTS; seguro só se TLS termination upstream for garantida.
  Documentar premissa ou adicionar middleware.
- `src/Hosts/Vinx.Erp.WorkerHost/InboundWhatsAppImageReceivedHandler.cs:55` —
  questão: helpers `Persist*DeadLetterAsync` copiados em três handlers
  (também `NotificationDeliveryRequestedHandler.cs:78`,
  `OwnerInvitationDeliveryRequestedHandler.cs:67`). Extrair helper compartilhado.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:43` — nit: nome de policy
  `"browser-session-login"` declarado duas vezes (:43 e
  `BrowserBffAuthenticationExtensions.cs:20`); usar constante única.
- `src/Hosts/Vinx.Erp.ApiHost/Program.cs:538` — nit: quinta cópia do loop manual
  de leitura limitada de body (também `BrowserBffSessionEndpoints.cs:55`,
  `AgentEndpoints.cs:61`, `AgentHttpContracts.cs:69`,
  `StrictJsonBodyAttribute.cs:33`). Extrair um helper.
- `src/Hosts/Vinx.Erp.ApiHost/Notifications/MetaWhatsAppWebhookEndpoints.cs:56` —
  nit: webhook não configurado responde 503 `whatsapp_unconfigured` a anônimos,
  revelando estado de deploy; responder 404/401 uniforme.
- `MetaWhatsAppWebhookEndpoints.cs:85,104,319` — nits: options resolvido duas
  vezes; `rawBody` parseado duas vezes (`ParseStatuses`/`ParseInboundMedia`);
  helper local sombreia nome do tipo BCL `CryptographicOperations`.
- `src/Hosts/Vinx.Erp.ApiHost/Identity/BrowserBffSessionEndpoints.cs:23` — nit:
  endpoint anônimo de antiforgery token sem rate limit.
- `src/Hosts/Vinx.Erp.ApiHost/Controllers/PaguaiController.cs:139` — nit:
  `IsTrimmed` duplicado de `AtendimentoController.cs:177`.
- `src/Hosts/Vinx.Erp.WorkerHost/Program.cs:244` — nit:
  `WorkerDatabaseOptions` duplica forma/chave de `FoundationDatabaseOptions`
  (`ApiHost/Program.cs:748`); consolidar na Foundation.

Verificado limpo nesta parte: comparação fixa-tempo de `X-Hub-Signature-256` e
`hub.verify_token`; cookies BFF `__Host-` HttpOnly/SameSite=Strict/Secure em prod;
antiforgery validando toda mutação com cookie; DataProtection fora de Development;
JWT com issuer/audience/lifetime/key pinados; envelopes de erro estáveis `{code}`
sem vazamento.

### Módulo Notifications (WhatsApp/Meta) — 18 achados

- `src/Modules/Vinx.Erp.Notifications/Providers/Meta/MetaWhatsAppMediaClient.cs:105`
  — risco: corpo todo bufferizado antes do cheque de tamanho; resposta chunked
  sem `Content-Length` contorna o pre-check e pode OOM o worker. Usar
  `ResponseHeadersRead` + cópia limitada.
- `MetaWhatsAppMediaClient.cs:80` — risco: URL assinada aceita por scheme https
  sem allowlist de host; SSRF depende de confiar no corpo do Graph. Restringir a
  hosts conhecidos (graph.facebook.com/lookaside).
- `src/Modules/Vinx.Erp.Notifications/Providers/Meta/InboundWhatsAppInteractionService.cs:191`
  — risco: crash/transiente entre envio (:191) e commit (:204) faz redelivery
  reenviar mensagem ao usuário; sem chave de idempotência no send. Persistir
  intenção antes do enviar ou documentar at-least-once.
- `InboundWhatsAppInteractionService.cs:139` — questão: catch-all converte
  defeitos inesperados em `ocr_failed`/`ai_failed` permanentes, mascarando bugs
  que a DLQ tiparia. Estreitar para exceções esperadas de provedor.
- `InboundWhatsAppInteractionService.cs:233` — questão: caption/transcrição
  cercados só por rótulos em prosa ("Caption:", "Transcription:") no prompt;
  usar delimitadores explícitos e instrução de conteúdo inerte.
- `MetaWhatsAppMediaClient.cs:41` — nit: `mediaId` interpolado na URL sem
  validação de charset; validar `^\d+$`.
- `MetaWhatsAppClient.cs:57` — nit: 429 entra no retry fixo (1s/5s/30s);
  `Retry-After` ignorado.
- `MetaWhatsAppMediaClient.cs:63` — nit: `mime_type` do Graph buscado, validado e
  descartado; tipo real vem do header do download. Retornar o metadado
  autoritativo.
- `MetaWhatsAppReplyMapper.cs:40` — nit: truncamento `[..4095]` pode partir par
  surrogate (emoji); aparar surrogate incompleto ou usar `StringInfo`.
- `NotificationDeliveryWorkerService.cs:104` — nit: `claim.Channel!` null-forgiving
  em métrica; cair para `"unknown"` como em :76.
- `InboundWhatsAppInteractionService.cs:293` — nit: `UndefinedFunction` mapeado a
  rejeição permanente; drift de schema não é rejeição de mensagem.
- `InboundWhatsAppInteractionService.cs:79` — questão: clientes newed inline por
  chamada (`MetaWhatsAppMediaClient`, `DocumentVisionOcr`,
  `OpenAiCompatibleChatClient`, `MetaWhatsAppClient`) fora do DI, sem seam de
  teste. Injetar factories.
- `MetaWhatsAppClient.cs:126` — questão: `SendAsync` duplica ~100 linhas de
  `SendTextAsync` e já divergiu (bug da prioridade 2). Extrair núcleo comum de
  envio+classificação.
- `MetaWhatsAppClient.cs:78` — questão: sondas manuais `JsonDocument.TryGetProperty`
  (:78, :199; também `MetaWhatsAppMediaClient.cs:54`) onde DTOs tipados cobrem.
- `MetaWhatsAppClient.cs:21` — nit: quarta cópia idêntica de
  `JsonSerializerOptions(JsonSerializerDefaults.Web)` (também
  `MetaWhatsAppMapper.cs:39`, `MetaWhatsAppReplyMapper.cs:26`,
  `MetaWhatsAppMediaClient.cs:23`). Um static interno compartilhado.
- `MetaWhatsAppProvider.cs:109` — nit: `AddParameter` triplicado no módulo
  (também `InboundWhatsAppInteractionService.cs:378`,
  `NotificationDeliveryWorkerService.cs:202`) mais boilerplate repetido de
  transação/comando. Um executor compartilhado de função SQL.

Verificado limpo: segredos nunca logados; replay deduplicado por constraint única
`(phone_number_id, message_id)`; double-send outbound guardado por attempt-token;
mídia só em memória com EXIF removido por re-encode; imagem validada por decode
Skia; SQL parametrizado; retries delegados a policies do Wolverine.

### Foundation Files/Platform + Tools — 26 achados

- `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadPolicy.cs:24` — risco:
  validação de filename rejeita só `/`, `\`, `\0`; caracteres de controle (\r\n\t)
  persistem em `OriginalFileName` e viram header injection quando existir endpoint
  de download. Rejeitar < 0x20 e 0x7f, pontos/espaços finais.
- `src/Foundation/Vinx.Erp.Foundation/Files/DocumentVerificationPolicy.cs:34` —
  risco: content-type comparado contra MIME declarado pelo próprio uploader; sem
  magic-byte sniffing, poliglota passa todos os gates exceto ClamAV por assinatura.
  Checar assinatura PDF/JPEG/PNG durante `InspectObjectAsync`.
- `src/Foundation/Vinx.Erp.Foundation/Files/ClamAv/ClamAvDaemonClient.cs:73` —
  bug: no fallback, `TcpClient` criado e não descartado se `ConnectAsync` lançar.
  Envolver em try/catch com Dispose.
- `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs:247` — nit:
  `ReadUrlAsync` aceita URI absoluta qualquer do response; exigir mesmo host e
  https da BaseAddress.
- `src/Foundation/Vinx.Erp.Foundation/Platform/SupabaseAdminUserVerifier.cs:82,107`
  — nit/questão: corpo de resposta sem teto de leitura (irmão usa 32 KiB);
  `JsonException` classifica operador válido como `InvalidUser` em vez de
  `ProviderUnavailable`.
- `src/Foundation/Vinx.Erp.Foundation/Files/FileStorage.cs:24` — nit: 5 de 8
  membros da interface sem consumidor de produção (`EnsurePrivateContainerAsync`,
  `GetObjectMetadataAsync`, `CreateSignedDownloadUrlAsync`, `ListObjectsAsync`,
  `DeleteObjectAsync`). Remover até existir consumidor.
- `src/Foundation/Vinx.Erp.Foundation/Files/StorageObjectInspection.cs:4` — nit:
  `MalwareScanStatus` sempre `Unavailable` no único produtor e nunca lido.
- `src/Foundation/Vinx.Erp.Foundation/Domain/FileRecord.cs:80` — nit:
  `IsQuarantined` constante `true`; remover ou derivar de `ProcessingState`.
- `src/Foundation/Vinx.Erp.Foundation/Files/MalwareScanVerdict.cs:8` — nit: doc
  lista reason code que nenhum código produz.
- `src/Foundation/Vinx.Erp.Foundation/Platform/PlatformOperatorAccessService.cs:55`
  — nit: `RequireMfaAssuranceAsync` duplica bloco de `EvaluateAsync`; extrair
  helper de checagem de operador.
- `SupabaseAdminUserVerifier.cs:157` — nit: `HasRequiredString`/
  `HasFalseBoolean`/`IsMissingOrNull` duplicados verbatim em
  `SupabaseAdminOwnerInvitationClient.cs:177-189`.
- `src/Foundation/Vinx.Erp.Foundation/Platform/OwnerInvitationProviderDeliveryService.cs:73,79,109`
  — nits: client newed por chamada em vez de typed client; tupla posicional
  sem nome atravessa `FinalizeAsync` como Item1..Item4; três cópias do wrapper
  begin/call/commit + CheckViolation→PermanentException.
- `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs:71` — questão:
  objeto baixado inteiro para hash e reaberto/streamed de novo no scan; dois
  downloads completos por confirmação. Hashear durante o stream do scan.
- `src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs:159` —
  questão: `pg_advisory_lock` de sessão com conexão aberta através de inspeção e
  scan clamd (até 30 s default); rajada de confirms prende slots do pool. Estreitar
  o escopo do lock ao re-check do finalize.
- `DocumentUploadService.cs:123` — questão: `DbUpdateException` genérico vira
  409 idempotente/integridade; filtrar SqlState de unique violation.
- `DocumentUploadService.cs:621` — questão: auditoria de negação grava
  action=errorCode; denials ficam injunáveis por comando. Manter nome do comando
  como action e errorCode como reason.
- `src/Foundation/Vinx.Erp.Foundation/Platform/PlatformOrganizationProvisioningService.cs:194`
  — questão: detecção de acesso negado por literal em `MessageText`; usar
  errcode/SQLSTATE convencionado.
- `OwnerInvitationProviderDeliveryService.cs:178` — questão: `provider_ambiguous`
  terminal sem reconciliação implementada (#211B); blip de rede trava onboarding
  do owner sem recuperação automática. Aterrar reconciliação ou comando de requeue.
- `src/Tools/Vinx.Erp.PlatformBootstrap/Program.cs:164,87` — nits: busca do root
  do repositório quebra em publish single-file (receber caminho SQL por config);
  HttpClient sem timeout (100 s default) onde o equivalente do worker usa 10 s.
- `src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs:189` e
  `SupabaseAdminOwnerInvitationClient.cs:98` — nits: travessia manual de
  `JsonElement` (incl. probing de três grafias de chave) onde DTO tipado com
  `JsonPropertyName` cobre.

Verificado limpo: sem path traversal (`ProviderKey` GUID-based com
`ValidateObjectKey`); sem SQL inseguro; service-role key fora de logs/erros; sem
IDOR (lookups por CompanyId sob RLS); tokens de convite delegados ao Supabase com
redirect allowlist.

### Módulos AI, Paguai, Atendimento — 28 achados

- `src/Modules/Vinx.Erp.AI/Guardrails/AgentOutputContract.cs:75,85` — risco:
  reason codes embutem nomes de propriedade controlados pelo modelo
  (`unknown_field:{name}`) que fluem para logs estruturados
  (`AgentRunner.cs:192-200`) e tag `reason` de métrica — cardinalidade alta e
  conteúdo não confiável. Hoje inalcançável (`StructuredJson` sempre null), mas a
  um wiring de distância. Reason codes fixos, nunca ecoar nomes de campo.
- `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleChatClient.cs:124`
  — risco: cancelamento do caller capturado e reempacotado como
  `OpenAiCompatibleProviderException(Cancelled)`; desconexão de cliente vira 503
  `model_unavailable` em vez de cancelamento. Rethrow
  `OperationCanceledException` quando `callerToken` cancelou.
- `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleOptions.cs:64`
  — questão: `ApprovedHosts` default vazio = qualquer host público aprovado;
  exigir allowlist não vazia em produção.
- `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/ProviderEgressPolicy.cs:164`
  — questão: redes proibidas omitem 6to4 `2002::/16` e Teredo `2001::/32`.
- `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleRequestMapper.cs:94`
  — questão: `FunctionCallContent`/`FunctionResultContent` passam por referência
  com `AdditionalProperties`, contradizendo a garantia documentada de remoção de
  metadata. Reconstruir copiando só CallId/Name/Arguments.
- `src/Modules/Vinx.Erp.AI/DocumentContext/DocumentVisionOcr.cs:51` — questão:
  validador JSON schema manual onde structured output do Microsoft.Extensions.AI
  (`GetResponseAsync<T>`) cobre enforcement.
- `src/Modules/Vinx.Erp.AI/DocumentContext/PageRasterizer.cs:17` — questão:
  `SemaphoreSlim` process-wide serializa todo raster; concorrência enfileira
  segurando streams e transações abertas. Limitar concorrência upstream ou shard
  após confirmar threading do PDFium.
- `src/Modules/Vinx.Erp.Paguai/PaguaiDraftService.cs:483,562` — questão: fator
  100m hard-coded independente de Currency; correto só enquanto BRL pinado
  (`CreatePaguaiDraftRequest.cs:35`). Derivar fator da moeda ou apagar branch
  legacy após janela de migração.
- `PaguaiDraftService.cs:93` — questão: State string livre sem máquina de
  transição; introduzir enum com transições guardadas quando aprovações chegarem.
- `src/Modules/Vinx.Erp.Paguai/PaguaiAntiforgeryRequiredMetadata.cs:7` — questão:
  marker nunca consultado pela middleware
  (`BrowserBffRequestMiddlewares.cs:116` lê só o metadata interno do host);
  enforcement real vem do fallback de cookie identity (:124-130). Fazer a
  middleware ler `IAntiforgeryRequiredMetadata` e apagar o atributo duplicado.
- `src/Modules/Vinx.Erp.AI/Providers/OpenAiCompatible/OpenAiCompatibleChatClient.cs:42,119`
  — nits: snapshot manual de 18 campos compartilha lista `ApprovedHosts` por
  referência e esquece campo futuro (usar record clone); resultado de `ToResult`
  descartado (`_ =`) só por efeito colateral de schema.
- `src/Modules/Vinx.Erp.AI/Providers/ChatClientAgentModel.cs:16` — nit: parâmetro
  selection ignorado; "confirmação de modelo pinado" nunca checada.
- `src/Modules/Vinx.Erp.AI/AgentTaskProfile.cs:30-32` — nit: `TurnLimit`,
  `MaxTotalTokens`, `AllowedTools` validados e consumidos em lugar nenhum.
  Cabear no runner ou remover.
- `src/Modules/Vinx.Erp.AI/AgentRunner.cs:151` — nit: path gated usa primeiro
  `TextContent`, ungated (:140) usa texto concatenado; unificar extração.
- `src/Modules/Vinx.Erp.AI/Providers/DeepSeek/DeepSeekRequestMapper.cs:26` e
  `DeepSeekResponseMapper.cs:61` — nits: facades mortas em produção (só testes);
  apagar ou cabear. `DeepSeekOptions.cs:60` duplica validator do core quase
  linha a linha; delegar.
- `src/Modules/Vinx.Erp.AI/DocumentContext/ImageCanonicalizer.cs:43` — nit: PNG
  encode completo antes do fallback JPEG; escolher formato alvo antes de codificar.
- `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentEndpoints.cs:61` — nit: body bufferizado
  com cap e `AgentHttpContracts.TryParse` relê num segundo buffer idêntico;
  parsear o MemoryStream já limitado.
- `src/Hosts/Vinx.Erp.ApiHost/Agent/AgentRunServiceCollectionExtensions.cs:63` —
  nit: `BaseAddress` inerte (SDK monta URI de `OpenAIClientOptions.Endpoint`).
- `PaguaiDraftService.cs:167,218,427` — nits: transações explícitas em leituras
  single-statement sob RLS; listagem sem paginação nem take-limit.
- `src/Hosts/Vinx.Erp.ApiHost/Models/CreatePaguaiDraftRequest.cs:30` — nit:
  literais de range duplicam `PaguaiAmountRules` e valor é checado três vezes
  (annotations, controller :103, serviço :40). Referenciar constantes e manter uma
  checagem server-side.
- `src/Modules/Vinx.Erp.Atendimento/AtendimentoCardService.cs:720` — nit:
  `RowVersion += 1` manual sobrescrito pelo `RowVersionInterceptor`; apagar.
- `AtendimentoCardService.cs:609` — nit: board inteiro da empresa sem paginação.
- `AtendimentoCardService.cs:597` — nit: round-trip no-op de
  `CorrelationContext(actor.CorrelationId).CorrelationId` (mesmo padrão em
  `AgentExecutionApplicationService.cs:304`).

Verificado limpo: sem injeção de SQL (LINQ/interpolação parametrizada, inclusive
insert de histórico em `AtendimentoCardService.cs:144`); chaves de API fora de
logs; sem IDOR cross-tenant (membership + capability + RLS + predicados por
empresa, Get retorna 404); PdfPig/PDFtoImage complementares sem sobreposição;
dinheiro em decimal ponta a ponta.

### Frontend + infra/scripts — 20 achados

- `supabase/config.toml:182,226,176` — risco: senha mínima 6 (abaixo do mínimo
  recomendado) e confirmação de e-mail desabilitada com signup aberto; combinação
  sobrevive a copy-paste para projeto real/staging. Subir length e travar antes de
  clonagem.
- `apps/web/src/features/auth/session-provider.tsx:77` — bug (prioridade 3 acima).
- `apps/web/src/features/notifications/notifications-client.ts:7` — nit: schema
  `href` aceita protocol-relative `//evil.com`; allowlist da página é a única
  barreira. Apertar schema para a mesma allowlist.
- `apps/web/src/features/paguai/paguai-draft-client.ts:79` — bug/question: status
  documentados (400/403/404/409/503) degradam a `"protocol"` quando o corpo falta
  ou aborta mid-read; mapear por status primeiro (mesmo defeito em
  `notifications-client.ts:71`).
- `apps/web/src/features/atendimento/atendimento-client.ts:84,62,63` — questão:
  único cliente BFF sem zod; casts `as` diretos. Validar como os irmãos.
- `apps/web/src/features/auth/session-provider.tsx:238` — nit: `signIn` colapsa
  rate_limited/unavailable/network numa mensagem "verifique credenciais" e força
  status anonymous, descartando a union discriminada.
- `apps/web/src/features/people/people-page.tsx:66` — questão: máquina de
  reconciliação de ~620 linhas (8 refs, generation counters, fases
  `pendingRefresh`) reimplementando mutation + `invalidateQueries` do TanStack
  Query; arquivo mais complexo do app para tela de create+list.
- `apps/web/src/features/paguai/paguai-page.tsx:136` (e `atendimento-page.tsx:47`,
  `notifications-page.tsx:101`, `notifications-provider.tsx:70`) — questão:
  lifecycle manual de fetch (guards, refs, epochs) onde `useQuery`/
  `useInfiniteQuery`/`refetchOnWindowFocus` cobrem corridas e cache.
- `apps/web/src/features/auth/login-page.tsx:29` e `paguai-page.tsx:57,179` —
  questão: validação manual com refs e parser regex de amount onde
  react-hook-form + zodResolver e o `amountSchema` zod já existentes cobrem.
- `apps/web/src/features/paguai/paguai-page.tsx:78` e
  `notifications-page.tsx:52` — nits: formatadores manuais onde
  `Intl.NumberFormat("pt-BR", {style:"currency",currency:"BRL"})`,
  `Intl.DateTimeFormat` e `Intl.RelativeTimeFormat` cobrem.
- `apps/web/src/features/notifications/notifications-page.tsx:137` — nit:
  `readAt` fabricado com clock do cliente pós-ack; ecoar valor do servidor.
- `people-page.tsx:102` e `platform-organization-page.tsx:103` — nit: refs
  escritos durante render; mover para effects.
- `apps/web/src/features/paguai/paguai-draft-client.ts:63` e
  `browser-session-client.ts:266` — questão: três convenções paralelas de fetch
  same-origin no app (`fetchSameOrigin`/`normalizeFailure` duplicados em quatro
  clientes, já divergindo no tratamento de falha de rede). Um transport
  compartilhado.
- `apps/web/src/app/feature-preparation-page.tsx:10` — nit: componente morto,
  exportado só para o próprio teste.
- `apps/web/src/app/app-route-manifest.tsx:161,200` — nit: dupla filtragem de
  rotas prototype (`import.meta.env.DEV` duas vezes).
- `app-route-manifest.tsx:69` — nit: rota `/kanban` renderiza página de outro
  módulo; usar placeholder de preparação.
- `apps/web/src/features/atendimento/atendimento-page.tsx:32` — nit: seletor de
  empresa lista memberships sem filtro de capability que paguai/people aplicam.
- `scripts/generate-foundation-schema-snapshot.sh:26-78` — nit: duplica
  `verify-foundation-database.sh:21-79` (spin-up, roles, senhas, normalização de
  dump); extrair lib compartilhada para as regras não divergirem.

Verificado limpo: sem segredo commitado (fixtures usam dummies óbvios); sem
`dangerouslySetInnerHTML`; tokens só em cookie via BFF; redirect pós-login com
allowlist (`resolveAppPath`); scripts SQL com `%I` e senhas aleatórias bindadas
em 127.0.0.1.

## Temas transversais de simplificação

1. **Fetch/query no FE:** consolidar as três convenções de fetch em um transport
   único e migrar lifecycles manuais para TanStack Query; maior ganho de LOC e
   menor superfície de corrida.
2. **JSON manual no backend:** trocar sondas `JsonDocument` por DTOs tipados
   (Meta clients, Supabase admin clients, ListObjects) e avaliar structured output
   do M.E.AI no OCR; um `JsonSerializerOptions` estático compartilhado.
3. **OTel decorativo:** decidir — cabear tracing/metrics de verdade
   (Wolverine/Npgsql/AspNetCore) ou remover os `AddOpenTelemetry()` vazios.
4. **Superfície morta:** `IFileStorage` 5/8 membros, facades DeepSeek,
   `TurnLimit`/`MaxTotalTokens`/`AllowedTools`, marker de antiforgery do Paguai,
   `feature-preparation-page`, `IsQuarantined`, `MalwareScanStatus` — PR de limpeza
   separado por área.
5. **Duplicações mecânicas:** loop de bounded read ×5, dead-letter persist ×3,
   `AddParameter`/transação ×3, validators/options duplicados, helpers de JSON
   admin ×2, script de snapshot vs verify.

## Sugestão de fatiamento (um PR vertical por fatia)

1. Correções de entrega WhatsApp (prioridade 2): classificação + exceção de
   transporte + teste de reconciliação.
2. Ingesta inbound (prioridade 1): outbox flush fail-closed ou marcador+sweeper.
3. Hardening do webhook anônimo (prioridade 4): cap durante cópia + rate limit
   por IP + 404 uniforme quando não configurado.
4. Egress AI (prioridade 5): `ConnectCallback` + allowlist obrigatória em prod +
   ranges 6to4/Teredo.
5. Sign-out fail-closed no FE (prioridade 3).
6. Upload de arquivos: magic-byte sniffing + charset de filename + dispose do
   TcpClient + escopo do advisory lock.
7. DLQ fail-closed no WorkerHost (interceptor) + partição de rate limit do MCP.
8. Limpeza FE: transport único + migração TanStack Query + formatters Intl.
9. Limpeza backend: superfície morta + deduplicações mecânicas + OTel decisão.
10. Supabase config: senha mínima e confirmação de e-mail (com checagem de onde
    o arquivo é clonado).

Cada fatia respeita uma issue/PR próprio conforme AGENTS.md; as fatias 1–7 são
independentes entre si e podem rodar em paralelo; 8–9 tocam muitos arquivos e
convém dividir por módulo dentro delas.
