# Casos de verificação — contrato do canal Telegram

Manifesto da fatia PR 1 (#307). Os casos abaixo cobrem o contrato SQL e o
fan-out da intenção; nenhuma chamada externa ao Telegram faz parte desta
fatia.

## Schema e funções

- [ ] As três tabelas de intenção/preferência/template aceitam `telegram` e
      preservam `in_app` e `whatsapp`.
- [ ] Um contato WhatsApp continua exigindo E.164 em `phone_number`; um
      contato Telegram usa somente `address`, com ID privado positivo,
      decimal canônico e dentro do limite documentado.
- [ ] Template Telegram exige texto aprovado, não vazio, sem espaços nas
      extremidades e entre 1 e 4096 caracteres.
- [ ] Funções SECURITY DEFINER preservam owner, `search_path`, grants e
      negação explícita a `PUBLIC`/`vinx_migrator`.

## Regressão e reversão

- [ ] A preferência Telegram é self-service, auditada somente em transição
      efetiva e não altera a preferência WhatsApp.
- [ ] A troca de endereço limpa `is_validated`; repetição do mesmo endereço é
      idempotente.
- [ ] O down migration recusa qualquer estado Telegram e conserva linhas
      existentes de WhatsApp/in-app.

## Intenção e outbox

- [ ] Opt-in cria a intenção Telegram e o alerta in-app na mesma transação;
      cada nova entrega publica somente um envelope com UUID opaco.
- [ ] Opt-out, template ausente/não aprovado e replay não publicam envelope
      Telegram; o in-app mantém sua semântica obrigatória.
- [ ] Falha de validação, autorização, auditoria, commit ou outbox não deixa
      intenção ou envelope parcial.

Os testes automatizados e os resultados serão vinculados a cada caso ao final
da Task 1.3.
