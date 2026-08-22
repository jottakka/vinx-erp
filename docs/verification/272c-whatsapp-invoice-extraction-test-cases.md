# #272 PR3 — WhatsApp inbound: extração tipada de fatura

Manifesto da fatia PR3 (empilhada no PR2/#274). Todos públicos, PostgreSQL
real via Testcontainers; nenhum caso persiste telefone, legenda, nome de
arquivo ou conteúdo em log/auditoria/DLQ. O agente genérico não participa:
a extração é um único turno de perfil de texto com contrato JSON estrito.

## Sucesso

| Caso | Teste |
| --- | --- |
| PDF com camada de texto produz candidato tipado (beneficiário, valor como texto da gramática decimal, BRL, vencimento, número) e mensagem concluída | `MetaWhatsAppInvoiceExtractionTests.Pdf_with_text_layer_yields_a_typed_candidate` |

## Fechamento needs_review (sem defaults silenciosos)

| Caso | Teste |
| --- | --- |
| Valor ausente → `missing_amount` | `...Missing_amount_closes_as_needs_review` |
| Valor fora da gramática decimal (`R$ 250,00`) → `invalid_amount` (sem reformatar/arredondar) | `...Amount_outside_the_decimal_grammar_closes_as_needs_review` |
| Vencimento ausente → `missing_due_date` | `...Missing_due_date_closes_as_needs_review` |
| PDF sem camada de texto → `no_text_layer` | `...Pdf_without_text_layer_closes_as_needs_review` |
| Perfis de IA ausentes → `extraction_unavailable` (documento permanece admitido) | `...Unconfigured_ai_profiles_close_extraction_as_needs_review` |

## Privilégios

| Caso | Teste |
| --- | --- |
| Worker executa `record_inbound_whatsapp_invoice_candidate`/`inbound_document_actor_is_allowed`; runtime sem acesso direto à tabela | `...Candidate_function_privileges_are_closed` |

## Regras da fatia

- needs_review **não** é falha da mensagem: o documento admitido carrega o
  resultado fechado da extração (`notifications.inbound_whatsapp_invoice_candidates`).
- Valor viaja como texto na gramática decimal do Pagaí (`^\d+(\.\d+)?$`);
  arredondamento e reformatização são proibidos — fora da gramática vira revisão.
- Pre-check de membership/capability (`inbound_document_actor_is_allowed`) antes
  de qualquer download/scan/extração; `capability_missing` continua fechado.
- Sem mutação Pagaí e sem resposta automática (PR4 e decisões pendentes).

## Fora de escopo

Rasterização/OCR de PDF sem camada de texto (PDFium), imagem como documento
(o corte de fatura é PDF; imagens seguem o subsete #140), resposta automática.
