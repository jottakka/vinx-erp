# Telegram inbound

## Provisionamento

1. Exponha `POST /foundation/notifications/telegram/webhook` em URL pública HTTPS.
2. Gere um segredo aleatório compatível com `A-Za-z0-9_-`, entre 1 e 256 caracteres, e guarde-o somente no secret store como `TELEGRAM_WEBHOOK_SECRET_TOKEN`.
3. Registre a URL e o segredo no Bot API com `setWebhook`, enviando o segredo no campo `secret_token`. Nunca coloque token do bot ou segredo em comando salvo, log ou documentação.
4. Confira a leitura do provedor com `getWebhookInfo`: URL HTTPS esperada, ausência de erro persistente e contagem de pendências compatível com a operação.

O ApiHost aceita inicializar sem esse segredo e responde `503 telegram_unconfigured`; um segredo presente mas fora da gramática impede a inicialização. A rota é pública por desenho, mas só interpreta o corpo depois de validar exatamente um header `X-Telegram-Bot-Api-Secret-Token`.

## Rotação e incidente

Para rotacionar, gere outro segredo no secret store, atualize `setWebhook` com a mesma URL e o novo `secret_token`, faça o deploy e valide `getWebhookInfo`. Durante divergência entre provedor e host, o retorno será `401`; não remova validação nem aceite dois segredos. Para `503`, confirme presença do segredo na configuração efetiva. Para repetição de entrega, preserve os dados: a admissão deduplica `(chat_id, message_id)` e responde 2xx para replay seguro. Conteúdo de mensagens, chat IDs e IDs de update não entram nos envelopes, metadados ou logs operacionais.

## Recibo do worker

O WorkerHost consome `telegram-inbound` com inbox durável e storage previamente
provisionado; `AutoCreate.None` continua obrigatório. O handler público recebe
somente `InboundMessageId` e chama
`notifications.complete_inbound_telegram_text(uuid)`. Apenas uma linha
`pending + admitted` muda para `completed`; replay de linha concluída é no-op,
linha ausente retorna `not_found` e qualquer resolução fechada permanece
`pending` como evidência.

`completed` significa **somente recibo técnico da fila**. Não confirma leitura,
resposta humana, decisão de negócio, entrega pelo Bot API nem aprovação por IA.
Esta etapa não lê o texto e não chama HTTP, provedor ou IA. Toda confirmação
humana ou de negócio continua sendo decidida pelo servidor em um fluxo próprio;
nunca deve ser inferida deste estado.

Somente `vinx_worker` executa a função de conclusão e acessa, com DML explícito,
as tabelas `wolverine_queue_telegram_inbound` e `_scheduled`. O runtime não
conclui diretamente e o worker não recebe `CREATE` no schema da fila.

Envelope vazio vai imediatamente para a DLQ como `telegram_inbound_invalid`.
Falha Npgsql transitória repete de forma finita após 1 s, 5 s e 30 s e termina
como `telegram_inbound_retry_exhausted`; falha inesperada termina como
`telegram_inbound_unexpected`. Antes da persistência, o corpo vira
`InboundTelegramTextReceived(Guid.Empty)`, headers são removidos e a mensagem
da exceção é estável. UUID original, header, texto, IDs do Telegram e detalhes
da falha não podem sobreviver no corpo ou na exceção da DLQ.
