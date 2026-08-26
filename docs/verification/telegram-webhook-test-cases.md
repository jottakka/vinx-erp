# Casos de teste — webhook Telegram

| Evidência BDD | Dado / quando / então |
|---|---|
| Autenticação antes do parse | Dado segredo ausente, errado ou múltiplo e corpo malformado, quando chega POST, então responde `503` ou `401` sem parse, linha ou envelope. |
| Contrato HTTP | Dado segredo válido, quando JSON é malformado ou grande demais, então responde `400` ou `413`; GET responde `405`. |
| Admissão privada | Dado texto privado, `from == chat`, remetente humano e vínculo único validado, quando chega POST, então responde `200`, grava uma evidência e publica um envelope. |
| Fechamento sem efeito | Dado grupo, canal, bot, IDs incompatíveis, formato não suportado ou ator não admitido, quando chega POST, então responde `200` sem envelope e sem seleção de empresa. |
| Estado e concorrência | Dado replay ou dois POSTs idênticos concorrentes, quando a chave `(chat_id, message_id)` colide, então existe no máximo uma linha e um envelope. Falha antes do commit não deixa nenhum dos dois. |
| Privacidade do outbox | Dado evento admitido, quando corpo e metadado da fila são lidos, então contêm somente `InboundMessageId` GUID opaco, sem texto, chat, update ou message ID. |

As provas automatizadas ficam em `TelegramWebhookTests`, `TelegramInboundWebhookTests` e `TelegramInboundOutboxTests`. A execução final mandatória é registrada pela PR após os dois gates seriais do controlador.
