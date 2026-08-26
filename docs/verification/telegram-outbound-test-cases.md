# Casos de verificação — outbound Telegram

Manifesto da fatia PR 2, tarefa 2.1 (#308). Esta etapa adiciona somente a
função SQL que resolve, de forma restrita, o destino de uma entrega Telegram.
As chamadas ao Bot API e os testes de integração do worker pertencem às
tarefas seguintes.

## Resolução do destino

- [ ] Uma entrega `telegram` em `provider_in_flight`, com o `provider_attempt_id`
      correto, template aprovado com `telegram_text` válido e contato Telegram
      validado retorna exatamente `address` como `chat_id` e o texto aprovado.
- [ ] Uma entrega de outro canal, outro `delivery_id`, outro `attempt_id`, outro
      template/empresa ou estado diferente retorna uma única linha com os dois
      campos nulos.
- [ ] Contato inexistente, não validado ou incompatível com a entrega nunca
      retorna endereço ou texto.
- [ ] Template inexistente, não aprovado, sem `telegram_text` ou com texto em
      branco nunca retorna endereço ou texto.
- [ ] Parâmetros nulos são rejeitados com erro de parâmetros inválidos; aliases
      explícitos evitam ambiguidade entre parâmetros, saídas e colunas.

## Acesso e reversão

- [ ] A função é `SECURITY DEFINER`, tem owner fixo
      `vinx_notifications_owner` e `search_path` fixo; somente `vinx_worker`
      possui `EXECUTE`, sem acesso para `PUBLIC` ou `vinx_migrator`.
- [ ] A função não usa ator, GUC de empresa, DML amplo ou dados fora do destino
      identificado pelos parâmetros.
- [ ] O down migration recusa downgrade enquanto existir qualquer preferência,
      template, intenção ou contato Telegram e, sem estado Telegram, remove
      somente esta função.

## Evidência

O gate público previsto para esta tarefa é:

```text
./scripts/verify-foundation-database.sh
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramOutboundTargetTests'
```

`TelegramOutboundTargetTests` será criado na tarefa 2.4; a coerência do
manifesto e a compilação/aplicação da migração são verificadas nesta etapa.
