# Relatório de implementação — tarefa 2.1

## Escopo

Implementada a tarefa 2.1 da PR 2 (#308), limitada ao alvo SQL outbound do
Telegram e ao manifesto de verificação em português. Nenhum provider HTTP,
host, teste de integração ou pacote foi adicionado.

## Arquivos alterados

- `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/20260826121000_AddTelegramOutboundTarget.cs`
  - Cria `notifications.get_telegram_notification_delivery_target(uuid, uuid)`.
  - Restringe a leitura a entrega Telegram em `provider_in_flight`, tentativa
    correspondente, contato Telegram validado e template Telegram aprovado
    com texto não nulo/não vazio.
  - Retorna `address` como `chat_id` e `telegram_text` como `message_text`.
  - Retorna exatamente uma linha com ambos os campos nulos quando não há alvo
    elegível.
  - Usa `SECURITY DEFINER`, `SET search_path TO pg_catalog, notifications,
    pg_temp`, owner `vinx_notifications_owner`, revogação para `PUBLIC` e
    `vinx_migrator`, e concede `EXECUTE` somente a `vinx_worker`.
  - O down recusa a reversão se existir qualquer preferência, template,
    intenção ou contato Telegram; sem estado Telegram, remove somente a nova
    função.
- `docs/verification/telegram-outbound-test-cases.md`
  - Registra em português os casos BDD de resolução, isolamento, acesso e
    reversão desta tarefa.

## Validação

- `git diff --check`
  - Resultado: sucesso, exit code 0.
- `./scripts/verify-foundation-database.sh`
  - Resultado: sucesso, exit code 0.
  - O modelo EF compilou, não havia alterações de modelo pendentes e a base
    descartável aplicou todas as migrações até
    `20260826121000_AddTelegramOutboundTarget` com sucesso.
- `dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~TelegramOutboundTargetTests'`
  - Resultado: exit code 0; 0 testes executados, pois a classe pertence à
    tarefa 2.4 e ainda não existe neste branch.

## Commit

- Commit inicial da tarefa: `c473123a1daf9f9e73d4471246f251ef688e1050`
- Mensagem: `feat(notifications): add Telegram outbound target #308`

O relatório é um commit de documentação separado para preservar o commit de
implementação acima sem reescrever seu hash.

## Preocupações

- A cobertura comportamental direta de `TelegramOutboundTargetTests` ainda não
  pode ser executada porque é um artefato explicitamente posterior da tarefa
  2.4. A aplicação da migração e a verificação de banco passaram.
