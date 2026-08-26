# Telegram inbound

## Provisionamento

1. Exponha `POST /foundation/notifications/telegram/webhook` em URL pública HTTPS.
2. Gere um segredo aleatório compatível com `A-Za-z0-9_-`, entre 1 e 256 caracteres, e guarde-o somente no secret store como `TELEGRAM_WEBHOOK_SECRET_TOKEN`.
3. Registre a URL e o segredo no Bot API com `setWebhook`, enviando o segredo no campo `secret_token`. Nunca coloque token do bot ou segredo em comando salvo, log ou documentação.
4. Confira a leitura do provedor com `getWebhookInfo`: URL HTTPS esperada, ausência de erro persistente e contagem de pendências compatível com a operação.

O ApiHost aceita inicializar sem esse segredo e responde `503 telegram_unconfigured`; um segredo presente mas fora da gramática impede a inicialização. A rota é pública por desenho, mas só interpreta o corpo depois de validar exatamente um header `X-Telegram-Bot-Api-Secret-Token`.

## Rotação e incidente

Para rotacionar, gere outro segredo no secret store, atualize `setWebhook` com a mesma URL e o novo `secret_token`, faça o deploy e valide `getWebhookInfo`. Durante divergência entre provedor e host, o retorno será `401`; não remova validação nem aceite dois segredos. Para `503`, confirme presença do segredo na configuração efetiva. Para repetição de entrega, preserve os dados: a admissão deduplica `(chat_id, message_id)` e responde 2xx para replay seguro. Conteúdo de mensagens, chat IDs e IDs de update não entram nos envelopes, metadados ou logs operacionais.
