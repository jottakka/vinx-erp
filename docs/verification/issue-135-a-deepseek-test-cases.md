# Manifesto público — issue 135-A, adapter DeepSeek v4 Flash

## Escopo e comandos

O módulo AI mantém somente o adapter DeepSeek por endpoint OpenAI-compatible
aprovado, atrás de `IChatClient`, sem registro DI, host ou API. Todos os testes
usam `HttpMessageHandler` fake: nenhum teste chama endpoint real ou exige
segredo.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q` |

## Cinco classes BDD

| Classe | Garantia | Evidência pública |
|---|---|---|
| sucesso | Perfil aprovado produz decisão final tipada com correlation ID | `DeepSeekChatClientContractTests.Approved_profile_produces_a_typed_final_decision_with_correlation_id` |
| sucesso | Proposta de leitura chega tipada e nenhuma ferramenta é invocada | `DeepSeekChatClientContractTests.Tool_call_response_becomes_a_proposal_and_the_adapter_never_invokes_tools` |
| sucesso | Conteúdo multimodal permitido (png) chega ao provider como image_url | `DeepSeekChatClientContractTests.Allowed_multimodal_content_reaches_the_provider_as_image_url` |
| sucesso | Usage reportado para o orçamento do runtime | `DeepSeekChatClientContractTests.Usage_is_reported_for_budget_accounting` |
| validação/erros | Argumentos de tool inválidos falham como provider_invalid sem invocar tool | `DeepSeekChatClientContractTests.Invalid_tool_arguments_do_not_invoke_tools_and_fail_as_provider_invalid`; `DeepSeekResponseMapperTests.Function_call_content_with_exception_is_rejected_as_provider_invalid` |
| validação/erros | Campos desconhecidos da resposta não quebram o contrato normalizado | `DeepSeekChatClientContractTests.Unknown_response_fields_do_not_break_the_normalized_contract` |
| validação/erros | Timeout e cancelamento do chamador são classificados, sem retry | `DeepSeekChatClientContractTests.Timeout_is_classified_once_without_retry`; `DeepSeekChatClientContractTests.Caller_cancellation_is_classified_as_cancelled_without_retry` |
| validação/erros | Entrada acima dos limites do perfil falha antes de qualquer chamada HTTP | `DeepSeekChatClientContractTests.Input_above_profile_limits_fails_before_any_provider_call`; `DeepSeekRequestMapperTests.Conversation_above_profile_message_limit_is_rejected` |
| validação/erros | Perfil inválido falha na validação sem ecoar a chave | `DeepSeekOptionsTests.Empty_api_key_fails_validation_without_echoing_the_key`; `DeepSeekOptionsTests.Budget_below_output_token_limit_fails` |
| acesso | Request não escolhe provider/modelo: a wire carrega somente o modelo do perfil | `DeepSeekChatClientContractTests.Request_cannot_choose_another_model_or_provider`; `DeepSeekChatClientContractTests.Runtime_mutation_of_the_options_object_is_rejected` |
| acesso | Limite de saída vem do perfil (clamp), nunca do request | `DeepSeekChatClientContractTests.Output_token_cap_is_enforced_from_the_profile` |
| acesso | Metadado de contexto confiável nunca chega à wire | `DeepSeekChatClientContractTests.Trusted_context_metadata_never_reaches_the_wire`; `DeepSeekRequestMapperTests.Trusted_context_metadata_is_stripped_from_messages` |
| estado/exceções | Falha externa permanece falha classificada (429/503/401), sem retry automático | `DeepSeekChatClientContractTests.Rate_limited_and_unavailable_responses_are_classified_and_never_retried` |
| estado/exceções | Mensagem de falha e logs são redacted: sem body, prompt ou chave | `DeepSeekChatClientContractTests.Failure_messages_are_redacted`; `DeepSeekChatClientContractTests.Error_logs_never_contain_body_prompt_or_key` |
| concorrência | Execuções paralelas isolam resposta, correlation e chamada | `DeepSeekChatClientContractTests.Parallel_runs_isolate_responses_budget_and_correlation` |
| contrato | Streaming fica fora do perfil aprovado | `DeepSeekChatClientContractTests.Streaming_is_outside_the_approved_profile` |

## Limite explícito

Esses testes provam o contrato do adapter com fronteira fake. Não provam
integração com o endpoint real (campo `max_completion_tokens`, limites de
rate/custo do serviço), exposição HTTP (#138), guardrails de saída (#136) nem
contexto confiável (#132). O registro DI e o HttpClientFactory pertencem à
fatia da API após o contrato estabilizar.
