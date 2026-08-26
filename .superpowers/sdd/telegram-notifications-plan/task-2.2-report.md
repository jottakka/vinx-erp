# Task 2.2 — Telegram options and Bot API client

## Resultado

Task 2.2 foi implementada na branch `codex/telegram-outbound`, sobre os
commits da PR 1 e da Task 2.1. O adapter permanece sem retry próprio e expõe
somente `NotificationProviderResult` ou a exceção transitória já existente.

## Arquivos alterados

- `src/Modules/Vinx.Erp.Notifications/Providers/Telegram/TelegramOptions.cs`
  - adiciona `BotToken`, `ApiBaseUrl` com default
    `https://api.telegram.org` e `WebhookSecretToken`;
  - mantém outbound ausente como estado desabilitado;
  - exige base HTTPS sem query, fragmento ou credenciais embutidas;
  - valida o segredo com `^[A-Za-z0-9_-]{1,256}$`;
  - mantém o token restrito aos caracteres seguros para o segmento de caminho.
- `src/Modules/Vinx.Erp.Notifications/Providers/Telegram/TelegramClient.cs`
  - adiciona `TelegramClient.HttpClientName = "telegram-bot"`;
  - envia `POST /bot{token}/sendMessage` somente no momento do envio;
  - serializa o corpo mínimo `chat_id`/`text`;
  - classifica aceite confirmado, rejeição, ambiguidade e falha transitória
    conforme o contrato da PR 2;
  - não registra URI, token, corpo, resposta ou descrição remota;
  - não possui retry;
  - declara `TelegramMessage`, reutilizável pela `TelegramMapper` da Task 2.3.
- `tests/public/Vinx.Erp.Foundation.UnitTests/TelegramContractTests.cs`
  - cobre defaults e estados de configuração;
  - cobre validação da base e do segredo;
  - usa handlers locais, sem rede e sem credenciais reais;
  - cobre método, caminho, corpo mínimo, 4xx, 408, `ok=false`, sucesso
    malformado, timeout, transporte e 429/5xx;
  - verifica que a exceção transitória não contém token, URI, corpo ou detalhe
    remoto.

## TDD e gates

### RED

Comando:

```text
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramContractTests'
```

Resultado: exit code `1`. O teste falhou na compilação porque o namespace
Telegram, `TelegramClient`, `TelegramOptions` e `TelegramMessage` ainda não
existiam. Esse foi o RED esperado antes do código de produção.

### GREEN

Comando executado após a implementação:

```text
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramContractTests'
```

Resultado: exit code `0`.

```text
Passed!  - Failed:     0, Passed:    28, Skipped:     0, Total:    28, Duration: 32 ms
```

Também foi executado `git diff --check`, com exit code `0`, antes do commit.

## Commit

`9565cbd feat(notifications): add Telegram Bot API client #308`

## Concerns e limites deliberados

- O registro do named client, `RemoveAllLoggers()`, desativação de redirects e
  cookies, validação all-or-nothing no WorkerHost e registro do provider ficam
  na Task 2.3, conforme o plano.
- O teste de logs do pipeline completo com `IHttpClientFactory` e o fake
  `telegram-bot` de integração ficam na Task 2.4. Neste slice, a proteção é
  demonstrada pela ausência de logging no cliente e pela sanitização da
  exceção transitória.
- `TelegramMessage` foi colocado no arquivo do cliente para que o contrato
  público compilasse e pudesse ser testado neste gate; a Task 2.3 deve manter
  esse tipo e fazer a `TelegramMapper` reutilizá-lo, sem criar um segundo tipo.
