# Casos públicos — Telegram Bot API outbound (#308)

Esta fatia entrega somente o envio de texto aprovado pelo WorkerHost. Testes
usam `FakeTelegramBotHandler`; não fazem rede e usam apenas placeholders.
Inbound, webhook, anexos, comandos e resposta automática permanecem fora do
escopo.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Entrega Telegram elegível em tentativa correta envia exatamente um `POST` com somente `chat_id` e `text`; `ok=true` e `message_id` positivo concluem `delivered` | `TelegramOutboundTests.Eligible_delivery_posts_the_minimal_body_once_and_logs_no_secret_or_content` | coberto |
| validação/estado | `400`, `ok=false`, `408` e 2xx malformado tornam-se rejeitado ou ambíguo terminal, sem confirmação falsa | `TelegramOutboundTests.Provider_response_classes_preserve_terminal_state_without_false_delivery` | coberto |
| validação/estado | Timeout e falha de transporte fazem uma chamada e fecham como `provider_ambiguous` | `TelegramOutboundTests.Timeout_and_transport_are_ambiguous_with_one_external_call` | coberto |
| retry | `429` e `5xx` usam as quatro tentativas finitas do worker, deixam `pending_provider` e nunca confirmam entrega | `TelegramOutboundTests.Transient_responses_use_the_finite_worker_retry_without_false_confirmation` | coberto |
| validação | Provider sem token/base permanece desabilitado e falha fechado antes de qualquer chamada externa | `TelegramOutboundTests.Disabled_provider_fails_closed_without_constructing_an_external_call` | coberto |
| validação | Contato Telegram não validado não produz alvo nem chamada ao Bot API, mesmo com provider configurado | `TelegramOutboundTests.Unvalidated_target_fails_closed_without_a_bot_API_call` | coberto |
| transporte | Redirect não é seguido; uma resposta `302` é rejeitada em uma única chamada | `TelegramOutboundTests.Redirect_response_is_not_followed_and_is_rejected_once` | coberto |
| configuração | Worker aceita Telegram ausente ou token com base padrão; rejeita configuração parcial/inválida, preserva providers InApp/Meta/Telegram e configura `telegram-bot` com timeout de 10s, sem redirect/cookies e sem logging HTTP padrão | `TelegramOutboundTests.Host_registers_all_providers_and_hardens_the_named_telegram_client`, `Worker_validates_Telegram_root_configuration_at_startup` e captura de logs do cenário de sucesso | coberto |
| acesso | A função de destino retorna dados somente para a tentativa Telegram `provider_in_flight` correta; tentativa antiga ou entrega terminal retorna nulos | `TelegramOutboundTargetTests.Worker_reads_only_the_matching_in_flight_telegram_target_and_no_other_attempt` | coberto |
| acesso | Contato não validado não vaza destino; `vinx_worker` é o único executor da função | `TelegramOutboundTargetTests.Target_function_requires_validated_contact_and_is_executable_only_by_worker` | coberto |
| concorrência | Duas chamadas do worker para a mesma entrega deixam somente o vencedor do claim chamar o Bot API | `TelegramOutboundTests.Concurrent_worker_calls_claim_once_and_send_once` | coberto |

O log capturado nunca contém token, URI do Bot API, corpo da mensagem ou
detalhe de resposta do provedor. `delivered` é aceite do provider, não prova de
leitura humana.

Comandos focados:

```text
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramContractTests'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramOutbound'
```
