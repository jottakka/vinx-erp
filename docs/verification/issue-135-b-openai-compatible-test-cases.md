# Manifesto público — issue 135-B, provider OpenAI-compatible genérico + perfil de visão

## Escopo e comandos

O módulo AI mantém o núcleo genérico OpenAI-compatible (`OpenAiCompatibleChatClient`
+ política de egress pura) e o DeepSeek como perfil nomeado text-only, sem
registro DI, host ou API. Todos os testes usam `HttpMessageHandler` fake:
nenhum teste chama endpoint real, resolve DNS ou exige segredo.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q` |

## Cinco classes BDD

| Classe | Garantia | Evidência pública |
|---|---|---|
| sucesso | Perfil genérico produz decisão final tipada com correlation ID no endpoint do perfil | `OpenAiCompatibleChatClientContractTests.Approved_profile_produces_a_typed_final_decision_with_correlation_id` |
| sucesso | Perfil de visão encaminha bytes de imagem como `image_url` (data URI) dentro dos tetos | `OpenAiCompatibleChatClientContractTests.Vision_profile_forwards_image_bytes_as_image_url_data_uri`; `OpenAiCompatibleRequestMapperTests.Image_bytes_within_the_caps_are_admitted_and_counted` |
| sucesso | Usage reportado para o orçamento do runtime | `OpenAiCompatibleChatClientContractTests.Usage_is_reported_for_budget_accounting` |
| validação/erros | Entrada acima dos limites falha antes de qualquer chamada HTTP | `OpenAiCompatibleChatClientContractTests.Input_above_profile_limits_fails_before_any_provider_call`; `OpenAiCompatibleRequestMapperTests.Conversation_above_profile_message_limit_is_rejected` |
| validação/erros | Imagem acima de contagem/bytes/admissão falha antes de HTTP com zero chamadas | `OpenAiCompatibleChatClientContractTests.Vision_profile_rejects_images_above_the_aggregate_limit_before_http`; `OpenAiCompatibleRequestMapperTests.Image_count_above_the_profile_limit_is_rejected` |
| validação/erros | Imagem embutida em data URI decodifica para bytes e é admitida; imagem por URL externa é irrepresentável no ME.AI (ctor exige data URI) e o mapper rejeita esquema não-data como defesa | `OpenAiCompatibleRequestMapperTests.Image_wrapped_in_a_data_uri_decodes_to_bytes_and_is_admitted` |
| validação/erros | Media type não aprovado é rejeitado, nunca descartado | `OpenAiCompatibleRequestMapperTests.Unsupported_media_type_is_rejected_on_a_vision_profile` |
| validação/erros | Instructions contam no teto de caracteres e falham antes de HTTP | `OpenAiCompatibleChatClientContractTests.Instructions_above_the_character_limit_fail_before_http`; `OpenAiCompatibleRequestMapperTests.Instructions_count_against_the_character_limit` |
| validação/erros | Perfil inválido na construção (modelo/chave/orçamento/tetos de visão) falha com exceção dedicada | `OpenAiCompatibleChatClientContractTests.Constructor_rejects_profiles_that_fail_full_validation` |
| validação/erros | Perfil inválido (endpoint/limites/porta/module tag/visão) falha sem ecoar a chave | `OpenAiCompatibleOptionsTests.Invalid_profiles_fail_validation`; `OpenAiCompatibleOptionsTests.Empty_api_key_fails_validation_without_echoing_the_key`; `OpenAiCompatibleOptionsTests.Vision_admission_estimate_above_the_call_budget_fails` |
| validação/erros | Contrato 2.2: perfil DeepSeek text-only rejeita imagem antes de HTTP | `DeepSeekChatClientContractTests.Image_content_is_rejected_before_any_http_call`; `DeepSeekRequestMapperTests.Image_content_is_rejected_because_the_hosted_profile_is_text_only`; `DeepSeekRequestMapperTests.Unsupported_media_types_are_rejected_instead_of_dropped` |
| acesso | Sem host/API nesta fatia (N/A — fronteira exposta e reautorização pertencem a #138); análogo testado: request nunca escolhe modelo/endpoint/porta | `OpenAiCompatibleChatClientContractTests.Request_cannot_choose_another_model_or_endpoint`; `OpenAiCompatibleChatClientContractTests.Constructor_rejects_endpoints_outside_the_egress_policy` |
| acesso | Egress: https, porta 443/aprovada, localhost, faixas proibidas (IPv4/IPv6/mapped) e grafias numéricas não padrão (hex/octal/quad parcial) rejeitados na construção | `ProviderEgressPolicyTests.Prohibited_address_ranges_are_rejected`; `ProviderEgressPolicyTests.Non_standard_port_is_rejected_unless_explicitly_approved`; `ProviderEgressPolicyTests.Non_standard_numeric_ip_spellings_are_rejected`; `OpenAiCompatibleOptionsTests.Invalid_profiles_fail_validation` |
| acesso | Allowlist de hosts com fronteira de ponto; sufixo embutido em outro domínio não passa | `ProviderEgressPolicyTests.Approved_hosts_match_exact_and_dot_boundary_suffix_only` |
| acesso | Metadado de contexto confiável nunca chega à wire | `OpenAiCompatibleChatClientContractTests.Trusted_context_metadata_never_reaches_the_wire`; `OpenAiCompatibleRequestMapperTests.Trusted_context_metadata_is_stripped_from_messages` |
| estado/exceções | Falha externa permanece falha classificada (429/503/401), sem retry automático | `OpenAiCompatibleChatClientContractTests.Rate_limited_and_unavailable_responses_are_classified_and_never_retried` |
| estado/exceções | Timeout e cancelamento do chamador são classificados, sem retry | `OpenAiCompatibleChatClientContractTests.Timeout_is_classified_once_without_retry`; `OpenAiCompatibleChatClientContractTests.Caller_cancellation_is_classified_as_cancelled_without_retry` |
| estado/exceções | Mensagem de falha e logs são redacted: sem body, prompt, chave ou endpoint de segredo | `OpenAiCompatibleChatClientContractTests.Failure_messages_are_redacted`; `OpenAiCompatibleChatClientContractTests.Error_logs_never_contain_body_prompt_or_key` |
| estado/exceções | A cadeia pública de exceção não carrega causa bruta (`ToString()`/`InnerException` sem body) | `OpenAiCompatibleChatClientContractTests.Failure_exception_chain_never_contains_raw_provider_body` |
| estado/exceções | Endereços resolvidos em faixa proibida reprovam (função exposta para o ConnectCallback de #138) | `ProviderEgressPolicyTests.Resolved_addresses_are_validated_per_address` |
| concorrência | Execuções paralelas isolam resposta, correlation e chamada | `OpenAiCompatibleChatClientContractTests.Parallel_runs_isolate_responses_budget_and_correlation` |
| contrato | Streaming fica fora do perfil aprovado | `OpenAiCompatibleChatClientContractTests.Streaming_is_outside_the_approved_profile` |

## Limite explícito

Esses testes provam o contrato do adapter com fronteira fake. Não provam
integração com endpoint real, exposição HTTP (#138), guardrails de saída
(#136), contexto confiável (#132) nem o pipeline OCR de #134. O
endurecimento de transporte em runtime (ConnectCallback por conexão,
redirects, proxy) e o `ValidateOnStart` pertencem à fatia da API. A
inspeção de dimensão/pixel decodado é follow-up registrado no plano.
