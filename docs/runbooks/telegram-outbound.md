# Runbook — outbound Telegram Bot API

Sem valores reais neste documento. Tokens e segredos vivem exclusivamente no
secret store do ambiente; não entram em `appsettings`, commits, logs, DLQ ou
evidência de teste.

## Configuração por ambiente

| Variável | Conteúdo |
|---|---|
| `TELEGRAM_BOT_TOKEN` | Token do bot criado pelo BotFather (segredo) |
| `TELEGRAM_API_BASE_URL` | Base HTTPS do Bot API; padrão `https://api.telegram.org` |
| `TELEGRAM_WEBHOOK_SECRET_TOKEN` | Segredo do webhook inbound, conforme `docs/runbooks/telegram-inbound.md` (segredo) |

> **Aviso:** `TELEGRAM_API_BASE_URL` deve apontar somente para um endpoint
> confiável, pois o token do bot trafega no caminho da URL.

O WorkerHost considera somente `TELEGRAM_BOT_TOKEN` e
`TELEGRAM_API_BASE_URL`: a ausência outbound mantém filas independentes
operacionais, e token/base parciais ou inválidos impedem o startup. Ele ignora
`TELEGRAM_WEBHOOK_SECRET_TOKEN`, inclusive quando está sozinho ou malformado;
o ApiHost valida esse segredo no webhook inbound
(`docs/runbooks/telegram-inbound.md`). Este runbook é somente outbound e não
registra webhook nem processa mensagens recebidas.

## Provisionamento e cadastro confiável

1. Crie o bot no BotFather e grave o token somente no secret store do ambiente.
2. Antes de cadastrar um chat privado, a pessoa deve iniciar voluntariamente a
   conversa com o bot. Um bot não consegue iniciar conversa com um usuário.
3. Obtenha e confira o chat ID por procedimento administrativo autenticado;
   valide a identidade da pessoa por canal confiável antes de marcar o contato
   como validado. Nunca aceite um chat ID enviado sem essa conferência.
4. Cadastre o ID decimal positivo em
   `notifications.set_recipient_contact(..., 'telegram', address)`. Alterar o
   endereço remove `is_validated`; a nova validação é explícita.
5. Mantenha por empresa um template Telegram aprovado, com `telegram_text`
   literal não vazio de 1 a 4096 caracteres, e o opt-in do destinatário.

## Operação e rotação

- O worker envia exclusivamente `chat_id` e `text` por `sendMessage`. A URL
  tokenizada é construída só no instante da chamada; o client não segue
  redirects e usa `.RemoveAllLoggers()` para remover os loggers padrão do
  `HttpClient` nomeado. Isso não desativa telemetria ou instrumentação externa
  autorizada.
- Estados: `pending_delivery → pending_provider → provider_in_flight →
  delivered|provider_failed|provider_ambiguous`. `429` e `5xx` liberam a
  tentativa para o retry finito do worker (1s/5s/30s); `408`, timeout,
  transporte incerto e resposta 2xx malformada fecham como
  `provider_ambiguous`, sem reenvio cego.
- Para rotacionar o token: gere/revogue pelo BotFather conforme o procedimento
  aprovado, atualize o secret store, reinicie o WorkerHost e confirme que não
  há token antigo em logs/observabilidade. Não cole tokens em tickets ou shell
  history.
- Para o webhook inbound em operação (ver
  `docs/runbooks/telegram-inbound.md`), consulte `getWebhookInfo` usando uma
  sessão operacional autorizada para diagnosticar URL, último erro e pendências;
  não coloque a resposta bruta em logs ou documentação. O envio outbound não
  depende de webhook.

## Incidentes

| Sintoma | Ação |
|---|---|
| Startup falha por options | Corrigir o secret store e a base HTTPS; nunca registrar o valor do token |
| `provider_failed` / `delivery_target_missing` | Conferir opt-in, template aprovado e contato Telegram validado; repetir cadastro não valida automaticamente |
| Muitos `provider_ambiguous` | Investigar disponibilidade do Bot API e reconciliar o efeito antes de qualquer novo envio |
| `429` ou `5xx` recorrente | Acompanhar o retry/DLQ redigida, reduzir a origem se necessário e resolver antes do replay controlado |
| Suspeita de token exposto | Revogar/rotacionar imediatamente, atualizar secret store e auditar acessos sem reproduzir o token |

`delivered` significa somente que o Bot API aceitou a mensagem com
`message_id` positivo. Não prova entrega no dispositivo, leitura humana,
compreensão, consentimento ou efeito de negócio.
