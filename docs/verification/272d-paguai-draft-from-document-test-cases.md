# #272 PR4 — Pagaí: rascunho idempotente a partir do documento inbound

Manifesto da fatia PR4 (empilhada no PR3/#275). Endpoint autenticado
`POST /paguai/requests/from-document`; o agente genérico e a automação ficam
fora — a criação automática de rascunho permanece decisão pendente do #272.

## Sucesso

| Caso | Teste |
| --- | --- |
| Candidato válido cria um único rascunho (valor/beneficiário/vencimento do candidato, campos não garantidos explícitos), replay idempotente devolve o mesmo rascunho e `source_inbound_message_id` vincula o documento | `PaguaiInboundDocumentDraftTests.Valid_candidate_creates_one_idempotent_draft_linked_to_the_document` |

## Fechamento

| Caso | Teste |
| --- | --- |
| Candidato `needs_review` → 400 (sem rascunho) | `...Needs_review_candidate_is_rejected` |
| Candidato de outra empresa (fora do escopo de membership do ator) → 400 | `...Candidate_of_another_company_is_not_visible` |
| Capability `paguai.draft.create` ausente → 403 (negação auditada pelo serviço existente) | `...Missing_capability_is_denied` |

## Privilégios

| Caso | Teste |
| --- | --- |
| `vinx_runtime` executa `read_inbound_whatsapp_invoice_candidate`; worker não; constraint única `uq_paguai_requests_source_inbound_message` presente | `...Read_function_privileges_are_closed` |

## Regras da fatia

- Idempotência dupla: registro de idempotência por ator (RequestId = id da
  admissão) + constraint UNIQUE `source_inbound_message_id` (um rascunho por
  documento, qualquer ator).
- Valor do candidato é revalidado na fronteira (`PaguaiAmountRules.TryParseText`);
  gramatura fora → 400, nunca reformato.
- `title`/`description`/`proofRequirement` são sempre explícitos no corpo da
  requisição — a fatura não os garante (decisão #272).
- Diferente ator para o mesmo documento esbarra na constraint única e reconcilia
  pelo caminho de idempotência existente (409 quando aplicável).

## Fora de escopo

Criação automática ao admitir candidato (decisão pendente), resposta automática
no WhatsApp, pagamento/aprovação/PIX e edição financeira.
