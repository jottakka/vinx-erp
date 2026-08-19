# Casos públicos — issue 28, WhatsApp Cloud API outbound (fatia W3)

## Escopo desta fatia

Adapter de saída da Meta Cloud API sobre o contrato do worker (#48): número
previamente cadastrado e E.164, WABA/phone por secret store, template aprovado
com variável única (deep-link), envio exclusivamente pelo worker, webhook de
status com assinatura verificada antes do parse e eventos deduplicados. Fora
desta fatia: inbound, bot, interpretação de mensagem, #140 e qualquer chamada
real à Meta (testes usam handler HTTP falso).

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado destinatário com opt-in, contato E.164 e template com mapeamento, quando o worker entrega, então `delivered` com exatamente uma chamada Graph cujo corpo carrega somente `to` e `template` aprovado + deep-link | `MetaWhatsAppOutboundTests.Eligible_recipient_receives_the_approved_template_with_minimal_payload` | coberto |
| validação | Dado destinatário sem contato cadastrado, quando o worker entrega, então `provider_failed` permanente sem nenhuma chamada externa | `MetaWhatsAppOutboundTests.Recipient_without_contact_fails_permanently_without_external_call` | coberto |
| validação | Dado template WhatsApp sem mapeamento Meta, quando o worker entrega, então permanente sem chamada externa | `MetaWhatsAppOutboundTests.Template_without_meta_mapping_fails_permanently_without_external_call` | coberto |
| validação/estado | Dado 5xx/rate-limit do Graph, quando o worker reprocessa, então 4 tentativas com release de lease e sem confirmação falsa (transitiva delegada ao #48) | `MetaWhatsAppOutboundTests.Graph_server_error_exhausts_retries_without_false_confirmation` | coberto |
| estado | Dado timeout do Graph, quando a chamada expira, então `provider_ambiguous` terminal com exatamente uma chamada — sem retry cego | `MetaWhatsAppOutboundTests.Graph_timeout_maps_to_terminal_ambiguous_with_one_call` | coberto |
| acesso (webhook) | Dado POST sem assinatura ou com corpo alterado, quando chega ao endpoint, então `401` antes de qualquer parse e zero efeitos | `MetaWhatsAppWebhookTests.Unsigned_or_tampered_post_is_rejected_before_any_side_effect` | coberto |
| acesso (webhook) | Dado challenge com verify token divergente, quando a Meta registra o webhook, então `403` sem echo | `MetaWhatsAppWebhookTests.Challenge_echoes_only_with_the_configured_verify_token` | coberto |
| estado (webhook) | Dado status assinado repetido, quando reprocessa, então evento único por `(message_id, event_type)` | `MetaWhatsAppWebhookTests.Signed_statuses_are_recorded_once_and_replays_do_not_duplicate` | coberto |
| estado (webhook) | Dado evento desconhecido ou fora de ordem, quando processa, então `200` sem gravação | `MetaWhatsAppWebhookTests.Unknown_or_out_of_order_events_are_ignored_without_error` | coberto |
| validação (webhook) | Dado corpo acima de 24 KiB, quando lê, então `413` sem ler além do limite | `MetaWhatsAppWebhookTests.Oversized_body_is_rejected_without_reading_past_the_limit` | coberto |
| contrato | Dado mapper com e sem deep-link, quando serializa, então somente `messaging_product/to/type/template` e no máximo uma variável de corpo | `MetaWhatsAppContractTests.Mapper_carries_only_the_recipient_template_and_optional_deep_link`; `MetaWhatsAppContractTests.Mapper_builds_the_deep_link_from_the_web_base_and_selector_only` | coberto |
| contrato | Dado 2xx com id, 4xx, 429/5xx e 408 do Graph, quando o client classifica, então Delivered/Rejected/transitiva/Ambiguous estáveis | `MetaWhatsAppContractTests.Client_classifies_stable_outcomes`; `MetaWhatsAppContractTests.Client_maps_server_errors_and_rate_limits_to_transient` | coberto |

## Limites mantidos

Nenhum dado sensível em payload, banco, log ou DLQ: o telefone vive somente em
`notifications.notification_recipient_contacts` (RLS) e é lido pela fronteira
estreita `get_notification_delivery_target` apenas na tentativa em curso; o
envelope Wolverine segue opaco. O `read` do webhook não é confirmação humana e
nada correlaciona ao domínio (Paguai incluso). Segredos somente por
secret store; testes com handler falso; integração real somente com ambiente
aprovado pelo responsável.
