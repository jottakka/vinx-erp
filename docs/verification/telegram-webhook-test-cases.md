# Casos de teste — webhook Telegram

| Evidência BDD | Dado / quando / então |
|---|---|
| Autenticação antes do parse | Dado segredo ausente, errado ou múltiplo e corpo malformado, quando chega POST, então responde `503` ou `401` sem parse, linha ou envelope. |
| Contrato HTTP | Dado segredo válido, quando JSON é malformado ou grande demais, então responde `400` ou `413`; GET responde `405`. |
| Admissão privada | Dado texto privado, `from == chat`, remetente humano e vínculo único validado, quando chega POST, então responde `200`, grava uma evidência e publica um envelope. |
| Fechamento sem efeito | Dado grupo, supergrupo, canal, business update, bot, IDs incompatíveis ou formato não textual, quando chega POST ao ApiHost real, então responde `200` sem linha, envelope ou seleção de empresa. Para unbound, unverified, inactive e ambiguous, a evidência fechada pode existir, mas nunca contém IDs resolvidos nem gera envelope. |
| Estado e concorrência | Dado replay ou dois POSTs HTTP idênticos concorrentes, quando a chave `(chat_id, message_id)` colide, então as duas respostas são 2xx e existe no máximo uma linha e um envelope. Uma falha de transação antes do commit não deixa nenhum dos dois; a repetição posterior recupera. |
| Privacidade do outbox | Dado evento admitido, quando payload decodificado, headers, body bruto e metadado da fila são lidos, então há somente `InboundMessageId` GUID opaco, sem texto, chat, update, message ID ou segredo. |

As provas automatizadas ficam em `TelegramWebhookTests`, `TelegramInboundWebhookTests` e `TelegramInboundOutboxTests`. A execução final mandatória é registrada pela PR após os dois gates seriais do controlador.
