# Casos de verificação — contrato do canal Telegram

Manifesto da fatia PR 1 (#307). Os casos abaixo cobrem o contrato SQL e o
fan-out da intenção; nenhuma chamada externa ao Telegram faz parte desta
fatia.

## Schema e funções

- [x] As três tabelas de intenção/preferência/template aceitam `telegram` e
      preservam `in_app` e `whatsapp`.
- [x] Um contato WhatsApp continua exigindo E.164 em `phone_number`; um
      contato Telegram usa somente `address`, com ID privado positivo,
      decimal canônico e dentro do limite documentado.
- [x] Template Telegram exige texto aprovado, não vazio, sem espaços nas
      extremidades e entre 1 e 4096 caracteres.
- [x] Funções SECURITY DEFINER preservam owner, `search_path`, grants e
      negação explícita a `PUBLIC`/`vinx_migrator`.

## Regressão e reversão

- [x] A preferência Telegram é self-service, auditada somente em transição
      efetiva e não altera a preferência WhatsApp.
- [x] A troca de endereço limpa `is_validated`; repetição do mesmo endereço é
      idempotente.
- [x] O down migration recusa qualquer estado Telegram e conserva linhas
      existentes de WhatsApp/in-app. A proteção é coberta pelo verificador de
      banco; a migração preserva o estado existente durante o upgrade.

## Intenção e outbox

- [x] Opt-in cria a intenção Telegram e o alerta in-app na mesma transação;
      cada nova entrega publica somente um envelope com UUID opaco.
- [x] Opt-out, template ausente/não aprovado e replay não publicam envelope
      Telegram; o in-app mantém sua semântica obrigatória.
- [x] Falha de validação, autorização, auditoria, commit ou outbox não deixa
      intenção ou envelope parcial.

Evidência automatizada: `TelegramNotificationIntentTests` cobre criação,
fan-out WhatsApp + Telegram, opt-out, template ausente/não aprovado, chave
inválida, limites do texto, endereço privado, preferência, escopo,
rollback, replay e concorrência. As suítes existentes
`NotificationIntentStateTests`, `NotificationIntentConcurrencyTests` e
`NotificationIntentOutboxTests` preservam as mesmas garantias para o contrato
pré-existente.
