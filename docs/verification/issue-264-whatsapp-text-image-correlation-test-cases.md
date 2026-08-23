# Casos BDD — correlação curta de texto e imagem WhatsApp (#264)

## Escopo

A fatia correlaciona no máximo um texto e uma imagem sem legenda durante 30
segundos, sem histórico ou resposta automática a texto isolado. A barreira
`reply_in_flight` do #265 permanece responsável pelo único `POST` automático.

| Classe BDD | Dado / quando / então | Evidência automatizada |
| --- | --- | --- |
| Sucesso | Dado texto e imagem únicos do mesmo ator, em qualquer ordem, quando a imagem processa, então OCR e texto entram como blocos rotulados em um único turno de usuário e há uma resposta. Imagem sem texto processa somente após o prazo. | `MetaWhatsAppInboundTests.Text_before_image_is_sent_with_ocr_in_one_ai_turn`; `MetaWhatsAppInboundTests.Image_before_text_is_sent_with_ocr_in_one_ai_turn`; `MetaWhatsAppInboundTests.Image_without_text_processes_as_image_only_after_the_deadline` |
| Validação e erros | Dado texto fora da janela ou texto sem imagem, quando o prazo termina, então o texto não entra no prompt, expira com motivo estável e não chama IA nem Meta. | `MetaWhatsAppInboundTests.Out_of_window_text_is_not_added_to_the_image_prompt`; `MetaWhatsAppInboundTests.Text_without_image_expires_without_ai_or_meta_calls` |
| Acesso | Dado runtime, worker e DLQ, então admissão fica no runtime, correlação é estreita para runtime/worker, expiração/claim ficam no worker e o novo envelope perde ID e headers antes da DLQ. | `MetaWhatsAppInboundAdmissionTests.Media_admission_function_privileges_are_closed`; `MetaWhatsAppInboundTests.Text_correlation_expiry_dead_letter_redacts_the_inbound_id` |
| Estado e exceções | Dado um prazo controlado, quando o handler executa diretamente, então realiza somente correlação/expiração, grava `correlation_timeout` quando aplicável e registra decisão estruturada sem conteúdo ou identificador. | `MetaWhatsAppInboundTests.Text_without_image_expires_without_ai_or_meta_calls` |
| Concorrência e replay | Dados remetentes concorrentes ou replay do mesmo par, quando o ingress correlaciona, então nenhum contexto atravessa remetentes e existe um par, um processamento e um POST. | `MetaWhatsAppInboundTests.Concurrent_senders_on_the_same_channel_never_exchange_text_context`; `MetaWhatsAppInboundTests.Text_and_image_replay_create_one_pair_and_one_internal_processing_result` |

Os testes usam timestamps controlados e invocação direta; nenhum teste espera
30 segundos. O corte exclui polling, timer próprio, novo broker, histórico,
chat, sessão e resposta automática a texto isolado.
