# Casos públicos — target de entrega de e-mail (#351)

Esta correção substitui aditivamente a função worker-only já aplicada em bancos
existentes. O único ajuste funcional é converter
`foundation.users.email` de `varchar(320)` para `text` no retorno SQL. O
provider, o cliente SMTP, a política de retry e as tabelas permanecem
inalterados; o SMTP falso anuncia somente o mecanismo `AUTH LOGIN` que
implementa.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado intent de e-mail aceito e tentativa corrente em voo, quando o worker lê o target, então recebe endereço, assunto, corpo e link exatos; o SMTP local aceito termina `delivered`, resolvido, com uma tentativa, uma mensagem e uma auditoria final | `NotificationEmailDeliveryTargetMigrationTests.Worker_reads_the_exact_target_only_for_the_current_email_attempt`; `EmailNotificationTests.Eligible_recipient_receives_the_approved_email_template` | coberto |
| validação/erros | Dado delivery inexistente, attempt antigo ou estado terminal, quando a função é chamada, então retorna a linha sentinela nula; autenticação SMTP rejeitada termina `provider_failed/provider_rejected`, resolvida, com uma tentativa, nenhuma mensagem e uma auditoria final | `NotificationEmailDeliveryTargetMigrationTests.Worker_reads_the_exact_target_only_for_the_current_email_attempt`; `EmailNotificationTests.Rejected_authentication_fails_permanently_without_retry` | coberto |
| acesso | Dadas as roles públicas, somente `vinx_worker` executa a função; runtime, migrator e PUBLIC não executam, e o worker não lê `users`, `notification_intents` ou `notification_templates` diretamente. Owner, `SECURITY DEFINER` e `search_path` fixo permanecem | `NotificationEmailDeliveryTargetMigrationTests.Worker_reads_the_exact_target_only_for_the_current_email_attempt` | coberto |
| estado/exceções | Dado banco parado na migration anterior com a função incompatível, a chamada válida falha com SQLSTATE `42804`; ao aplicar a migration de reparo, o mesmo target passa a ler. O Down restaura explicitamente o predecessor e mantém a mesma fronteira de acesso | `NotificationEmailDeliveryTargetMigrationTests.Applied_database_upgrades_to_the_repair_and_down_restores_the_worker_only_predecessor` | coberto |
| concorrência | Dado replay ou dois workers para a mesma entrega, somente o claim vencedor chama o provider e cria a auditoria final; estado terminal ou tentativa alheia não cria segunda entrega | `NotificationWorkerTests.Concurrent_worker_services_produce_one_delivery_and_one_provider_call`; `NotificationWorkerTests.Delivery_in_flight_by_another_attempt_is_skipped_without_provider_call`; sentinelas do teste direto de target | coberto pelo contrato compartilhado |

Os testes usam PostgreSQL e SMTP descartáveis locais; não fazem chamada a
provider real e não concedem acesso direto de tabela ao worker.
