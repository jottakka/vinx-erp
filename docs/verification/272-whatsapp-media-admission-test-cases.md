# #272 — WhatsApp inbound: admissão e identidade de imagem/documento (PR1/4)

Manifesto de casos de teste da fatia PR1. Todos os casos são públicos, rodam
contra PostgreSQL real (Testcontainers) pelo webhook assinado, e cobrem o
contrato fechado de admissão e identidade. Nenhum caso persiste telefone,
legenda, conteúdo de arquivo ou payload bruto.

## Sucesso

| Caso | Teste |
| --- | --- |
| Remetente validado + `image` válida → admitida com ids resolvidos e `pending` | `MetaWhatsAppInboundWebhookTests.Signed_image_message_creates_a_pending_admitted_inbound_row` |
| Remetente validado + `document` PDF válido → admitida, sem download/OCR/rascunho | `MetaWhatsAppInboundAdmissionTests.Validated_document_sender_is_admitted_without_processing` |
| Tipo desconhecido (`text`, `audio`) → ignorado sem admissão | `MetaWhatsAppInboundWebhookTests.Non_media_messages_are_ignored_without_any_row`, `MetaWhatsAppInboundAdmissionTests.Unknown_message_type_is_ignored_without_admission` |

## Validação e erros

| Caso | Teste |
| --- | --- |
| `messageId` vazio, `wa_id` ausente ou `phone_number_id` ausente → não avança | `MetaWhatsAppInboundAdmissionTests.Missing_channel_metadata_or_identifiers_do_not_advance` |
| `phone_number_id` desconhecido (≠ configurado) → não avança | `MetaWhatsAppInboundAdmissionTests.Unknown_channel_phone_number_id_is_not_admitted` |
| Remetente sem contato → `unbound` | `MetaWhatsAppInboundAdmissionTests.Sender_without_contact_is_unbound` |
| Contato sem `is_validated` → `unverified` | `MetaWhatsAppInboundAdmissionTests.Unvalidated_contact_is_unverified` |
| Documento com múltiplas empresas → `ambiguous` | `MetaWhatsAppInboundAdmissionTests.Contact_in_multiple_companies_is_ambiguous` |
| Payload malformado → 400 sem persistência parcial | existente em `MetaWhatsAppWebhookTests` (assinatura/parse) |

## Acesso e tenant

| Caso | Teste |
| --- | --- |
| Membership revogada → `inactive`, sem admissão de negócio | `MetaWhatsAppInboundAdmissionTests.Revoked_membership_is_inactive` |
| Empresa resolvida é somente a do contato validado; nenhuma outra empresa é criada/exposta | asserts de `ResolvedCompanyId`/`ResolvedMembershipId` nos casos admitidos/ambíguos acima |

## Estado e exceções

| Caso | Teste |
| --- | --- |
| Falha de banco → 5xx sem admissão parcial; reprocessamento seguro após recovery | `MetaWhatsAppInboundAdmissionTests.Database_failure_leaves_no_partial_admission_and_allows_reprocessing` |
| Nenhum estado Pagaí é criado | garantido por ausência de referência no caminho (sem mutação financeira no PR1); coberto por revisão |

## Concorrência e idempotência

| Caso | Teste |
| --- | --- |
| Webhook duplicado → única admissão | `MetaWhatsAppInboundWebhookTests.Replay_of_the_same_webhook_does_not_duplicate_the_inbound_row` |
| Entrega simultânea do mesmo `messageId` → uma única linha | `MetaWhatsAppInboundAdmissionTests.Concurrent_admission_of_the_same_message_produces_a_single_row` (EXECUTE paralelo da função pública) |
| Mesmo `messageId` com payload incompatível → conflito seguro sem duplicar | `MetaWhatsAppInboundAdmissionTests.Incompatible_replay_for_the_same_message_id_registers_a_safe_conflict` |

## Privilégios

| Caso | Teste |
| --- | --- |
| `vinx_runtime` executa `resolve_inbound_whatsapp_actor`/`admit_inbound_whatsapp_media`; `vinx_worker` não; `record_inbound_whatsapp_message` removida | `MetaWhatsAppInboundAdmissionTests.Media_admission_function_privileges_are_closed` |

## Divergências registradas

- **"Capability ausente falha fechado"**: o PR1 não cria estado de negócio
  além da admissão; não existe capability definida para admissão de mídia.
  A exigência aplica-se à mutação Pagaí (PR4, ex. `paguai.draft.create`).
- **Mudança de comportamento do #140**: imagens de remetentes não admitidos
  (antes respondidas pela IA) passam a não avançar — consequência direta da
  regra "somente `admitted` avança" do PR1. Os testes de e2e existentes agora
  semeiam contato validado.
- **`phone_number_id` conhecido** = exatamente o `META_WHATSAPP_PHONE_NUMBER_ID`
  configurado (deploy de número único). Vínculo canal→empresa e política de
  validação de contato seguem pendentes de aprovação (#272 decisões).
