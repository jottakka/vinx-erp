# Runbook — SMTP email outbound (canal de notificações por e-mail)

Sem valores reais neste documento; credenciais vivem exclusivamente no secret
store do ambiente.

## Configuração (por ambiente)

| Variável | Conteúdo |
|---|---|
| `SMTP_EMAIL_HOST` | Host do servidor SMTP (segredo de infra, não de negócio) |
| `SMTP_EMAIL_PORT` | Porta (padrão `587` com STARTTLS) |
| `SMTP_EMAIL_USE_TLS` | `true` (STARTTLS) ou `false` (teste loopback apenas) |
| `SMTP_EMAIL_USERNAME` | Usuário de autenticação SMTP |
| `SMTP_EMAIL_PASSWORD` | Senha de autenticação SMTP (segredo) |
| `SMTP_EMAIL_FROM_ADDRESS` | Endereço remetente (ex.: `noreply@exemplo.com`) |
| `SMTP_EMAIL_FROM_NAME` | Nome de exibição do remetente (opcional) |
| `SMTP_EMAIL_TIMEOUT_SECONDS` | Timeout do transporte (padrão `10`) |

WorkerHost valida as options em `ValidateOnStart` com tudo-ou-nada: o worker
sobe sem SMTP (cada entrega de e-mail falha fechada como `provider_unconfigured`),
mas configurado-porém-parcial derruba o boot. Segredos nunca em `appsettings`,
variável de commit ou log.

## Provisionamento

1. Segredos no secret store; nunca versionados.
2. Migrations Foundation aplicadas pelo migrador (canal `email` em
   `notification_templates`, `notification_intents` e
   `notification_preferences`).
3. Templates: insira em `notifications.notification_templates` a linha de canal
   `email` com `email_subject` e `email_body_html` preenchidos e
   `is_approved = true`.
4. Opt-in: o destinatário ativa o canal `email` por
   `notifications.set_channel_preference` (self-service), como o WhatsApp.

## Comportamento de falha

- Autenticação recusada, endereço inválido ou payload rejeitado: `provider_failed`
  (definitivo, sem retry cego).
- Falha transitiva de transporte (conexão recusada, rede, DNS): transitiva
  delegada ao retry finito do worker.
- Timeout ou conexão interrompida durante o envio: `provider_ambiguous`
  (terminal, sem retry cego).

## Verificação

- Cobertura: `tests/public/Vinx.Erp.Foundation.UnitTests/EmailProviderOptionsContractTests.cs`
  (política de endereço e options) e
  `tests/public/Vinx.Erp.Foundation.IntegrationTests/EmailNotificationTests.cs`
  (entrega real contra servidor SMTP falso em loopback, opt-out e recusa de
  autenticação).
