# Casos BDD — contexto confiável e runner do agente (#127+#132)

## Escopo e comandos

Esta matriz cobre a base mínima do módulo `Vinx.Erp.AI`: catálogo server-side
do perfil aprovado `read_only_summarize_v1`, política pura de contexto
confiável fail-closed e runner policy-antes-modelo com desfecho normalizado.
Os 44 casos do módulo usam fakes de modelo e resolver; nenhum teste chama
provider real, executa ferramenta ou altera a Foundation. O contexto
(`AgentExecutionContext`) e o desfecho (`AgentOutcome`) nunca carregam prompt,
JWT, e-mail ou segredo.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests` |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests` |

FU roda somente como regressão da fatia (Foundation intocada). ARQ também
verifica as regras de dependência do módulo em `AiModuleDependencyTests`.

## Cinco classes BDD

| Classe BDD | Cmd | Dado / quando / então | Resultado exigido | Evidência |
|---|---|---|---|---|
| sucesso | AU | Dado contrato aprovado de `read_only_summarize_v1`, quando `CreateValidated` constrói o perfil com esses valores, então o perfil carrega exatamente esses valores. | Journey `summarize`, escopo `Company`, capability `ai.read`, `ModelSelection` nulo, timeout 30 s, `TurnLimit` 1, orçamento 8.000 e `AllowedTools` vazio. | `AgentTaskProfileTests.CreateValidated_accepts_read_only_summarize_v1_values` |
| sucesso | AU | Dado ator autenticado com uma empresa e capability `ai.read` em escopo `company`, quando a política avalia o contexto, então o snapshot resolve a empresa pedida. | Status `Resolved` com `CompanyId` da empresa, `ai.read` nas capabilities, `CorrelationId` do ator e `ProfileId` do perfil. | `AgentContextPolicyTests.Authenticated_actor_single_company_receives_that_company_context` |
| sucesso | AU | Dado ator com memberships em Alfa e Beta, quando pede Alfa, então o contexto carrega somente Alfa. | `Resolved` com empresa, nome e membership de Alfa; nenhuma propriedade do resultado menciona Beta. | `AgentContextPolicyTests.Actor_with_multiple_companies_receives_only_requested_company` |
| sucesso | AU | Dado dois atores distintos com a mesma empresa e o mesmo perfil, quando cada um resolve o contexto, então cada contexto casa 1:1 com o seu ator. | `CorrelationId` e `ActorId` de cada contexto vêm do ator correspondente e são distintos entre atores. | `AgentContextPolicyTests.Correlation_id_flows_from_actor` |
| sucesso | AU | Dado perfil com `ModelSelection`, contexto resolvido e mensagem válida, quando o runner executa, então o modelo é chamado uma única vez. | `Completed/completed` com texto, `CorrelationId` do ator e `Elapsed`; turn normalizado com uma única mensagem `user` exatamente com o texto enviado e a seleção aprovada. | `AgentRunnerTests.Success_flow_calls_model_once_with_user_message` |
| sucesso | AU | Dado modelo que responde com tool call `tool_x`, quando a resposta volta, então a proposta nunca é executada. | `RequiresConfirmation/tool_proposal_requires_confirmation` com `ProposedActionName = "tool_x"` e sem texto; a fronteira `IAgentModel.GetResponseAsync` não recebe ferramentas. | `AgentRunnerOutcomeTests.Tool_proposal_requires_confirmation_and_never_executes` |
| validação | AU | Dado perfil com `ProfileId`, `Journey` ou `RequiredCapabilityName` vazio ou em branco, quando valida ou constrói validado, então rejeita (teoria: 6 casos). | `ArgumentException` com `ParamName` do campo rejeitado, tanto em `Validate` quanto em `CreateValidated`. | `AgentTaskProfileTests.Validate_rejects_empty_profile_id_journey_and_capability` |
| validação | AU | Dado timeout zero ou negativo, quando valida, então rejeita (teoria: 2 casos). | `ArgumentException` com `ParamName` do `Timeout`. | `AgentTaskProfileTests.Validate_rejects_non_positive_timeout` |
| validação | AU | Dado `TurnLimit` 0 ou 2 — fora do aprovado 1 —, quando valida, então rejeita (teoria: 2 casos). | `ArgumentException` com `ParamName` do `TurnLimit`. | `AgentTaskProfileTests.Validate_rejects_turn_limit_above_one_or_below_one` |
| validação | AU | Dado orçamento total de tokens zero ou negativo, quando valida, então rejeita (teoria: 2 casos). | `ArgumentException` com `ParamName` do `MaxTotalTokens`. | `AgentTaskProfileTests.Validate_rejects_non_positive_budget` |
| validação | AU | Dado escopo fora do enum `AgentJourneyScope`, quando valida, então rejeita. | `ArgumentException` com `ParamName` do `Scope`; `Company` é o único nome do enum. | `AgentTaskProfileTests.Validate_rejects_company_scope_without_company_scoped_requirement` |
| validação | AU | Dado identificador desconhecido ou em branco, quando o catálogo tenta resolver o perfil, então não resolve (default deny). | `TryGet` devolve `false` e perfil nulo para `inventado` e `" "`; o aprovado devolve journey `summarize`, escopo `Company` e `ai.read`. | `AgentTaskProfileTests.Catalog_returns_approved_profile_and_rejects_unknown` |
| validação | AU | Dado perfil aprovado do catálogo, quando verificado, então a política somente leitura está travada. | `AllowedTools` vazio e `TurnLimit` 1 — trava de regressão: mudança de política exige revisão explícita. | `AgentTaskProfileTests.Catalog_profile_is_read_only` |
| validação | AU | Dado record `AgentTaskProfile` construído, quando o cliente tenta mutar após a construção, então não consegue. | Tipo selado e todo setter com modreq `IsExternalInit` (init-only); nenhum valor é injetável. | `AgentTaskProfileTests.Client_cannot_inject_profile_values` |
| validação | AU | Dado mensagem vazia/em branco ou perfil nulo, quando `ValidateRequest` roda, então devolve o reason estável. | `message_empty` e `profile_unknown`; entrada válida devolve `null`. | `AgentContextPolicyTests.ValidateRequest_returns_reason_for_empty_message_and_unknown_profile` |
| validação | AU | Dado mensagem `"  "` no runner, quando executa, então falha antes de qualquer consulta ou modelo. | `Invalid/message_empty` sem texto, com `CorrelationId` do ator; resolver e modelo com `CallCount` 0. | `AgentRunnerTests.Empty_message_returns_invalid_without_resolution_or_model` |
| validação | AU | Dado `profileId` inexistente no catálogo server-side, quando executa, então falha antes de resolução e modelo. | `Invalid/profile_unknown`; resolver e modelo com `CallCount` 0. | `AgentRunnerTests.Unknown_profile_returns_invalid_without_model` |
| validação | AU | Dado contexto resolvido, quando inspecionado por reflexão, então expõe somente as 7 propriedades seguras. | Allowlist exata de `ActorId` a `Capabilities`; campo novo — prompt, JWT, e-mail, segredo — quebra o teste. | `AgentExecutionContextContractTests.Context_exposes_only_safe_properties` |
| validação | AU | Dado mensagem sensível e token de teste, quando o contexto é materializado e convertido, então nada vaza. | `ToString()` não contém a mensagem nem o segredo; o contexto é construído do snapshot e jamais recebe prompt ou credencial. | `AgentExecutionContextContractTests.Context_default_tostring_contains_no_message_or_secret` |
| validação | AU | Dado membership com `ai.read` (escopo `company`) e `reports.export` (`self`, `company`), quando o contexto resolve, então `Capabilities` é conjunto mínimo de nomes. | `IReadOnlySet<string>` com exatamente `["ai.read", "reports.export"]`, sem escopos, ids ou extras. | `AgentExecutionContextContractTests.Capability_names_are_minimal_strings` |
| validação | AU | Dado desfecho do runner, quando inspecionado por reflexão, então carrega somente campos seguros. | Allowlist exata: `Status`, `ReasonCode`, `Text`, `ProposedActionName`, `CorrelationId`, `Elapsed`; prompt, contexto, JWT ou PII quebram o teste. | `AgentRunnerOutcomeTests.Outcome_carries_only_safe_fields` |
| validação | ARQ | Dado `docs/modules/ai.md` reescrito nesta fatia, quando o teste de contrato de documentação roda, então contém os headings obrigatórios. | `ModuleContractDocumentationTests` exige os cinco headings de contrato do módulo (`Ownership`, `Company scope`, `Contract version`, `Deletion strategy`, `Migration notes`). | `ModuleContractDocumentationTests.Module_contract_contains_required_headings` |
| acesso | AU | Dado journey de empresa sem empresa pedida (`RequestedCompanyId` nulo), quando a política avalia, então pede esclarecimento. | `NeedsClarification/company_required` sem contexto. | `AgentContextPolicyTests.Company_scoped_journey_without_company_needs_clarification` |
| acesso | AU | Dado snapshot com Alfa e pedido de empresa Gamma fora dele (forjada), quando a política avalia, então falha fechado. | `Denied/company_not_authorized` sem contexto e sem dado autorizado; a empresa forjada não aparece em nenhuma propriedade do resultado. | `AgentContextPolicyTests.Forged_company_fails_closed_without_authorized_data` |
| acesso | AU | Dado membership ativa sem a capability `ai.read`, quando a política avalia o pedido da própria empresa, então nega. | `Denied/capability_denied` sem contexto. | `AgentContextPolicyTests.Missing_capability_denies` |
| acesso | AU | Dado resolução `NeedsClarification` no runner, quando executa, então o desfecho espelha o reason sem tocar o modelo. | `NeedsClarification/company_required` sem texto; modelo com `CallCount` 0. | `AgentRunnerTests.Missing_company_returns_needs_clarification_without_model` |
| acesso | AU | Dado resolução `Denied` no runner, quando executa, então o desfecho espelha o reason sem tocar o modelo. | `Denied/company_not_authorized` sem texto; modelo com `CallCount` 0. | `AgentRunnerTests.Denied_company_returns_denied_without_model` |
| acesso | AU | Dado os caminhos curtos de validação, resolução e provider, quando o runner conclui cada um, então nenhuma chamada de modelo acontece antes de `Resolved`. | Assert transversal dos caminhos curtos: modelo com `CallCount` 0 ao final dos três. | `AgentRunnerTests.No_model_call_happens_before_resolution` |
| estado | AU | Dado snapshot inicial com Alfa e Beta e revogação de Alfa antes da segunda avaliação, quando a política consome cada snapshot fresco, então o acesso concedido não é reutilizado. | Primeira avaliação `Resolved`; segunda `Denied/company_not_authorized` sem contexto — a política não mantém cache. | `AgentContextPolicyTests.Revoked_membership_not_reused` |
| estado | AU | Dado duas execuções do mesmo ator com snapshot revogado entre elas, quando a segunda roda, então falha fechado sem nova chamada de modelo. | Primeira `Completed`; segunda `Denied/company_not_authorized`; resolver chamado 2x e modelo 1x. | `AgentRunnerTests.Revoked_membership_between_runs_denies_second_run` |
| estado | AU | Dado snapshot de autorização indisponível, quando a resolução roda, então o runner devolve indisponibilidade sem modelo. | `Unavailable/context_unavailable`; modelo com `CallCount` 0. | `AgentRunnerTests.Unavailable_context_returns_unavailable_without_model` |
| estado | AU | Dado perfil aprovado com `ModelSelection` nulo e modelo registrado, quando executa, então falha fechado até o provider ser atribuído. | `Unavailable/provider_not_configured`; modelo com `CallCount` 0, mesmo com cliente presente. | `AgentRunnerTests.Provider_not_configured_returns_unavailable_without_model` |
| estado | AU | Dado modelo que lança exceção, quando a chamada falha, então vira indisponibilidade normalizada. | `Unavailable/model_unavailable` sem texto nem proposta; exceção preservada no log de erro. | `AgentRunnerOutcomeTests.Model_exception_returns_unavailable` |
| estado | AU | Dado perfil com timeout curto e modelo que demora além dele, quando o token vinculado dispara, então o desfecho é timeout, não exceção. | `TimedOut/model_timeout` encerrando antes do atraso total (`Elapsed` < 2 s); cancelamento vem do token vinculado, não do externo. | `AgentRunnerOutcomeTests.Model_timeout_returns_timed_out` |
| estado | AU | Dado token externo já cancelado pelo chamador, quando executa, então o cancelamento propaga. | `OperationCanceledException` lançada ao chamador — cancelamento externo não vira outcome do agente. | `AgentRunnerOutcomeTests.External_cancellation_propagates` |
| concorrência | AU | Dados dois atores com empresas e fakes próprios executando em paralelo, quando as runs concluem, então contexto e desfecho não se misturam. | Cada `Completed` casa 1:1 com o texto, `CorrelationId` e ator correspondentes; 1 chamada de resolver e de modelo por runner — o runner é stateless. | `AgentRunnerIsolationTests.Concurrent_actors_do_not_mix_context_or_outcome` |
| concorrência | AU | Dado o mesmo ator com Alfa e Beta no snapshot executando duas runs paralelas com empresas distintas, quando concluem, então cada run recebe a sua empresa. | Cada resolver recebe exatamente a sua `CompanyId`; cada modelo recebe exatamente a sua mensagem; respostas pareadas com o pedido de cada run. | `AgentRunnerIsolationTests.Concurrent_same_actor_different_companies` |

## Lacunas conhecidas

- O adapter `AgentContextResolver` — o caminho `context_unavailable` contra o
  `AuthorizedCompanySetResolver` real — não tem cobertura unitária direta: a
  classe da Foundation é selada e ligada ao banco, sem seam de teste. A
  política pura e o runner estão cobertos acima; a cobertura de integração
  fica para a PR da API (#138).
- A capability `ai.read` ainda não tem seed no banco: existe como constante
  `AgentCapabilities.Read` no módulo, com UUIDv5 canônico documentado no
  código. Sem seed, a produção falha fechado (default deny); a seed vem com
  #138.
- O perfil aprovado `read_only_summarize_v1` shipa sem `ModelSelection`: a
  produção devolve `unavailable/provider_not_configured` até #135 atribuir o
  provider aprovado.

Provider real, API supervisionada, UI e execução confirmada de ferramentas
não existem nesta fatia e pertencem a #135–#138.
