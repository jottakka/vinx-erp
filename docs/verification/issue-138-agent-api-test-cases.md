# Casos BDD — API supervisionada do agente (#138)

## Escopo e comandos

Esta matriz cobre a fatia da API `POST /agent/runs` no ApiHost
(`src/Hosts/Vinx.Erp.ApiHost/Agent/`): endpoint autenticado com parse estrito,
serviço de aplicação com idempotência e auditoria, composição server-side
(`AddAgentRunSurface`) e fail-closed sem provider. Os testes rodam contra
PostgreSQL real (Testcontainers) com o actor admitido pelo session gate interno
e um fake de `IAgentModel` injetado no DI do teste (padrão do
`ControlledMalwareScanner`): nenhum teste chama provider externo, e o fake
registra as chamadas para provar que validação, negação de contexto e 401
acontecem ANTES do modelo. Nenhum prompt real aparece em fixture ou asserção; o
texto programado do fake é sintético (domínios `.example`).

| Código | Comando exato |
|---|---|
| INT | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~AgentEndpointTests"` |
| DOC | `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter "FullyQualifiedName~DocumentUploadEndpointTests"` |
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests` |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests` |

INT fecha com 14 casos (os 11 cenários do plano; validação de contrato e
idempotência/auditoria subdivididos em testes próprios). O caso de timeout leva
~30 s de parede por design: o cancelamento vem do timeout de 30 s do perfil
aprovado do catálogo, não de uma option de teste. DOC roda como regressão do
padrão de factory estendido (`FoundationApiFactory` ganhou overrides de
`IAgentModel`/`AgentRunOptions`). AU, FU e ARQ fecham como regressão; o contrato
HTTP puro do parser já é coberto por `AgentHttpContractTests` (FU) desde a Task 2.

## Cinco classes BDD

| Classe BDD | Cmd | Dado / quando / então | Resultado exigido | Evidência |
|---|---|---|---|---|
| sucesso | INT | Dado actor com membership e `ai.read` (escopo Company) na empresa Alfa e fake retornando texto com e-mail, quando chama `POST /agent/runs`, então devolve `result`. | 200; `reasonCode=ok`; texto com `[email-redigido]` e sem o endereço; `sources=null`; correlation no corpo igual ao header `X-Correlation-ID`; fake chamado 1× com a seleção server-side (`openai_compatible`/modelo de teste), nunca do request. | `Valid_run_returns_result_with_redacted_text_and_correlation` |
| sucesso | INT | Dado o mesmo `requestId` e o mesmo payload, quando repete a chamada, então devolve o replay. | Mesmos status/reason/texto/correlation do primeiro; fake permanece em 1 chamada; 1 registro de idempotência (`agent.run`) cujo `ResultJson` carrega somente a resposta segura (com marcador de redação, sem prompt e sem e-mail bruto); 1 único evento de auditoria. | `Same_requestId_replays_same_body_without_a_second_call` |
| sucesso | INT | Dado proposta de tool do modelo, quando a fronteira responde, então devolve estado opaco. | 200 `requires_confirmation`/`tool_proposal_requires_confirmation`; `text=null`; o nome da ação proposta não aparece em corpo, `ResultJson` nem metadata de auditoria; fake chamado 1× — não existe caminho de execução. | `Requires_confirmation_returns_opaque_state_without_execution` |
| sucesso | INT | Dado actor autorizado em duas empresas e duas chamadas concorrentes com `requestId`s distintos, quando ambas concluem, então cada desfecho fica na sua empresa. | Duas 200 `result`; correlations distintas; conjunto de textos exatamente o programado (sem mistura nem perda); 2 registros, um por empresa; 2 auditorias, cada uma com a empresa do seu request. | `Parallel_requests_isolate_outcomes_and_correlations` |
| sucesso | INT | Dado uma execução com sucesso e outra negada por capability na mesma fábrica, quando lê a auditoria, então os dois eventos carregam somente metadata segura. | Sucesso: `Source=Ai`, escopo Company, `reason=ok`, metadata `{status:"result", reasonCode:"ok"}` sem e-mail; negação: `reason=capability_denied` com metadata `{status:"denied", reasonCode:"capability_denied"}` sem o prompt; nenhum `ResultJson` ecoa mensagem. | `Audit_records_success_and_denied_with_safe_metadata_only` |
| validação | INT | Dado mensagem em branco (somente espaços), quando chama o endpoint, então o parser estrito rejeita antes do serviço. | 400 `validation_error`/`message_empty`; `text=null`; fake 0 chamadas; nenhum evento de auditoria e nenhum registro de idempotência. | `Empty_message_returns_validation_error_without_provider_call` |
| validação | INT | Dado campo desconhecido no JSON (`extra`), quando chama o endpoint, então rejeita fechado. | 400 `validation_error`/`invalid_request`; fake 0 chamadas; sem auditoria e sem registro. | `Unknown_json_field_is_rejected` |
| validação | INT | Dado mensagem acima de 4000 caracteres após trim, quando chama o endpoint, então rejeita por tamanho. | 400 `validation_error`/`message_too_long`; fake 0 chamadas; sem auditoria. | `Message_too_long_is_rejected` |
| validação | INT | Dado jornada company-scoped (`read_only_summarize_v1`) sem `companyId`, quando chama e repete a mesma chamada, então pede esclarecimento em cada tentativa. | 200 `needs_clarification`/`company_required` em ambas; `text=null`; fake 0 chamadas; sem registro de idempotência (o escopo exige empresa) e 2 eventos de auditoria Global — retentativa sem empresa re-auda em vez de replay. | `Missing_company_for_company_scoped_journey_needs_clarification_on_every_retry` |
| acesso | INT | Dado `companyId` de empresa existente sem membership do actor (forjada), quando chama o endpoint, então nega antes do modelo. | 200 `denied`/`company_not_authorized`; `text=null`; fake 0 chamadas; auditoria `Denied` com escopo Company e metadata sem o prompt. | `Forged_company_denies_without_consulting_the_model` |
| acesso | INT | Dado fábrica JWT bearer sem token no cliente, quando chama o endpoint anonimamente, então o session gate interno nega. | 401 com corpo vazio; nenhuma sessão admitida; nenhum evento de auditoria. | `Anonymous_request_is_unauthorized` |
| estado | INT | Dado o mesmo `requestId` com payload divergente após um sucesso, quando chama novamente, então conflita auditado. | 409 `conflict`/`idempotency_conflict`; `text=null`; fake permanece em 1 chamada; 1 registro; auditorias `[Success, Denied]` com metadata de conflito. | `Different_payload_same_requestId_conflicts_409` |
| estado | INT | Dado composição SEM provider configurado (produção default), quando um actor autorizado chama o endpoint, então falha fechado. | 503 envelope `{"code":"provider_not_configured"}`; sem registro de idempotência e sem auditoria — 5xx não é durável e o `requestId` pode retentar. | `Provider_not_configured_returns_503_provider_not_configured` |
| estado | INT | Dado fake com atraso de 50 s, quando o timeout de 30 s do perfil aprovado cancela a chamada, então a fronteira mapeia para gateway timeout. | 504 envelope `{"code":"agent_timeout"}` (nunca `model_timeout`); sem registro e sem auditoria. | `Timeout_maps_to_504_agent_timeout` |

## Ordem completa exercitada (endpoint → gate)

Autenticação (session gate interno) → rate limit `foundation` → parse estrito
do corpo (cap por bytes, campos fechados, tipos exatos) → idempotência (replay
ou conflito 409) → runner: validação de entrada → resolução de contexto
confiável (membership + `ai.read` escopo Company; empresa forjada nega sem
consultar o modelo) → gate de provider (fail-closed `provider_not_configured`)
→ UMA chamada de modelo com seleção server-side → `AgentOutputGate`
(contrato→injeção→fontes→grounding→redação) → resposta tipada; persistência de
idempotência e auditoria `AuditSource.Ai` na mesma transação. 5xx não é durável.

## Lacunas conhecidas

- A capability `ai.read` (exigida pelo perfil `read_only_summarize_v1`) não
  possui seed em migração: os testes semeiam a linha localmente. Enquanto a
  seed de produção não existir, todo request autorizado cai em
  `capability_denied` em ambientes sem a linha — a superfície permanece
  fail-closed (default deny), sem exposição indevida.
- O caso 10 do plano ("2 atores/empresas concorrentes") executa com um actor e
  duas empresas autorizadas no MESMO banco: a fábrica de teste autentica um
  único principal por host, e dois hosts separados isolariam os dados em
  bancos distintos (asserção vazia). A concorrência real — índice único de
  idempotência por empresa, contexto RLS concorrente e correlations distintos —
  fica coberta no mesmo processo e na mesma base.
- O replay exige `companyId`: requests sem empresa (`needs_clarification`)
  re-auditam escopo Global a cada retentativa em vez de devolver replay —
  comportamento aceito na Task 3, capturado na matriz acima.
- Rate limit (429) e cancelamento externo do request HTTP não têm caso INT
  próprio nesta fatia: o limiter é compartilhado com `/foundation` (coberto
  lá) e o cancelamento externo propaga sem resposta tipada por construção.
