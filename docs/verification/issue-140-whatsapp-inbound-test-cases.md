# Test cases — #140 subsete: inbound imagem → OCR → IA → resposta

Data: 2026-08-19 · Branch: `codex/issue-140-whatsapp-inbound` (stacked no W3 #257)

## Contrato (unit)

| # | Caso | Expectativa |
| --- | --- | --- |
| U1 | Mapper de resposta monta envelope mínimo | `messaging_product/to/type/text.body`, body com trim |
| U2 | Truncamento no limite do WhatsApp | 4096 chars com `…` final, nunca acima |
| U3 | Serialize só expõe as 4 chaves | sem dados extras no payload |
| U4 | Entradas vazias rejeitadas | `ArgumentException` |

## Webhook (integração, assinatura HMAC válida)

| # | Caso | Expectativa |
| --- | --- | --- |
| W1 | POST assinado com mensagem `type=image` | linha `pending` com `message_id/wa_id/media_id/caption` |
| W2 | Replay do mesmo POST | 200 e nenhuma segunda linha (UNIQUE `message_id`) |
| W3 | Mensagem `type=text` | 200 e nenhuma linha (fora do corte) |

## Worker (integração, fakes: Graph + perfis IA)

| # | Caso | Expectativa |
| --- | --- | --- |
| K1 | E2E feliz | `completed` com `reply_message_id`; OCR 1 chamada; IA de texto recebe legenda+transcrição como DADOS; POST de resposta `type=text` com o texto da IA |
| K2 | Replay do webhook antes do worker | processa 1×, 1 resposta |
| K3 | Worker sem perfis de IA | `failed(ai_unconfigured)`, zero chamadas externas |
| K4 | Mídia rejeitada (404) | `failed(media_unavailable)`, sem resposta |
| K5 | Mídia 5xx uma vez | retry transitivo (≥2 downloads) e depois `completed` |

## Segurança / propriedades cobertas

- Assinatura sobre bytes brutos antes do parse (herdado W3; W1–W3 re-executados).
- Conteúdo do usuário (legenda + OCR) nunca é instrução: system fixa, dados rotulados
  (K1 asserca que a transcrição viaja como dado no request de texto).
- Dedup por `message_id` no ingress (W2/K2) e finalize idempotente por `reply_message_id`.
- Falhas terminais com motivo estável e sem eco de conteúdo; DLQ redigida por tipo.
- Canonicalização: EXIF descartado no re-encode; tetos de pixel/byte rejeitam antes do OCR.

## Fora do alcance deste corte

Teste contra Graph real e provedor de IA real (política do repo: handlers falsos; smoke
real é operação manual do responsável com credenciais por env).
