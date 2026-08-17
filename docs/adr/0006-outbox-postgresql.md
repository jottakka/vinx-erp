# ADR 0006: Primeiro outbox durável em PostgreSQL

## Status

Accepted

## Contexto

A arquitetura aprovada requer que a alteração de estado de negócio e a intenção
de trabalho assíncrono sejam persistidas na mesma transação PostgreSQL, sem
broker na primeira entrega. A recuperação do processo, concorrência entre
workers, retry, fila de mensagens sem solução (DLQ) e chamada a provedores
externos são preocupações posteriores; uma confirmação de entrega fora do
banco pode se perder ou chegar depois de um timeout.

Em scratch descartável, em 2026-08-16, o restore e o build em .NET 10
confirmaram compatibilidade sem conflito de EF Core/Npgsql entre
WolverineFx.Postgresql e WolverineFx.EntityFrameworkCore 6.28.2, e
MassTransit.EntityFrameworkCore 9.2.0. Isto é somente evidência comparativa:
não instala dependências nem introduz configuração, runtime, tabelas ou
mensagens neste repositório.

As referências oficiais confirmam que Wolverine persiste mensagens em
PostgreSQL e integra middleware transacional com EF Core; também deixam claro
que `UseEntityFrameworkCoreWolverineManagedMigrations()` entrega o schema do
`DbContext` ao Weasel, em vez da cadeia de migrations do EF Core. O outbox
transacional do MassTransit é alternativa madura, com bus/consumer outbox,
inbox e coordenação para múltiplos serviços de entrega, mas adiciona uma
topologia orientada a transporte que ainda não é necessária.

## Decisão

As futuras entregas #26 e #48 adotarão Wolverine como primeira opção de
outbox PostgreSQL sem broker. A escolha reduz a superfície inicial para a
intenção transacional e o worker local, mantendo MassTransit como alternativa
reavaliável, não como dependência instalada. A #26 é somente dona da intenção
e outbox transacionais.

As guardas obrigatórias dessa adoção são:

- #26 e #48 devem provar, conforme sua responsabilidade, que
  `AutoBuildMessageStorageOnStartup = AutoCreate.None` (bloqueia a construção
  automática do storage de mensagens no startup) e
  `OverrideAutoCreateResources(AutoCreate.None)` (bloqueia o provisionamento
  automático dos recursos do persistence PostgreSQL retornado por
  `PersistMessagesWithPostgresql`) impedem criação ou alteração automática de
  recursos no startup. As guardas têm escopos distintos e ambas são
  intencionais.
- EF é exclusivo do schema de negócio VINX/Foundation e de qualquer intenção
  própria. Esses recursos são criados ou alterados somente por migrations EF
  revisadas. `FoundationDbContext`, suas migrations e seu histórico EF
  continuam fonte canônica desse schema.
- `UseEntityFrameworkCoreWolverineManagedMigrations()` não pode gerir
  `FoundationDbContext`; #26 e #48 devem prová-lo. Weasel não será usado
  nesse contexto.
- Storage e envelopes técnicos do Wolverine são separados do schema
  VINX/Foundation e só podem ser provisionados ou alterados no deploy
  explícito, nunca em startup ou runtime. O storage relacional do Wolverine é
  aplicado por `db-apply`, executado somente com identidade migradora/deploy;
  pode usar Weasel somente para o schema técnico Wolverine. `resources setup`
  não é substituto: pode provisionar recursos além do storage relacional
  configurado e fica fora desta adoção. O mesmo deploy revisa o
  ownership e concede os grants mínimos por identidade de runtime: produtor e
  worker recebem somente os privilégios de DML, USAGE e EXECUTE de que
  precisam. A liberação exige provas positivas desses privilégios, provas
  negativas de DDL e ownership e `db-assert` validando o storage.
- Runtime e worker não possuem DDL nem ownership dos schemas técnicos
  Wolverine. #26 prova os grants mínimos do produtor e #48 os do worker, cada
  qual com provas positivas e negativas, antes de uso.
- A outbox contém somente identificadores internos opacos, tipo de evento e
  correlação mínima. O worker consulta dados necessários em fronteira estreita
  e autorizada.

Esta spike não executou nenhuma dessas provas futuras de configuração, deploy,
`db-assert`, DDL ou ownership.

## Limites de entrega e falha externa

A #26 implementará intenção e outbox transacionais e provará rollback sem
intenção/outbox e commit atômico. A #48 implementará somente infraestrutura
genérica de worker, lease/concorrência, retry, DLQ e observabilidade, incluindo
prova por fake ou contrato de provider de que dois workers não concluem duas
vezes a mesma mensagem internamente. Ela não fará chamada real a provider de
convite.

A #211B, posterior à #48, é a única dona do adapter Supabase Admin de convite
e da reconciliação real de `provider_ambiguous`. Ela vincula apenas
`issuer+subject` confirmado, nunca email. A #28, posterior à infraestrutura
genérica da #48, é dona do adapter Meta/WhatsApp, opt-in, templates e da
integração específica do webhook de canal assinado. Ela também reconcilia
`provider_ambiguous` do Meta de modo idempotente e observável, usando status,
webhook ou identificador do provider quando disponíveis, sem retry cego. A
#48 conserva apenas o contrato genérico de worker e estado de entrega; nenhum
desses adapters ou webhooks específicos pertence a esta spike.

Timeout externo com resultado desconhecido é `provider_ambiguous`. Ele exige
reconciliação idempotente e decisão observável; é proibido retry automático
cego. Deduplicação interna e o outbox não prometem exactly-once externo: o
provedor pode ter aceitado a operação antes de a confirmação se perder.

É proibido guardar PII, segredo ou payload bruto na outbox, DLQ, log ou
auditoria. Logs e auditoria preservam somente contexto mínimo, seguro e
correlacionável.

## Consequências e reavaliação

- A primeira entrega continua sem broker e sem promessa de entrega externa
  exactly-once.
- A operação precisa de migrations EF revisadas para VINX/Foundation e
  intenções próprias, deploy técnico Wolverine explícito, observabilidade de
  estado e reconciliação para `provider_ambiguous` na #211B ou na #28,
  conforme o provider.
- MassTransit será reavaliado se surgir broker, saga ou necessidade que
  justifique sua topologia e licença. Mudança de licença ou de topologia do
  MassTransit também exige reavaliação explícita antes de adoção.
- Esta ADR não autoriza instalar pacote, alterar `WorkerHost`,
  `FoundationDbContext`, migration, configuração, endpoint, provider,
  capability ou UI.

## Fontes

- [Wolverine: PostgreSQL integration](https://wolverinefx.net/guide/durability/postgresql.html)
- [Wolverine: EF Core transactional middleware](https://wolverinefx.net/guide/durability/efcore/transactional-middleware.html)
- [Wolverine: EF Core and Weasel-managed migrations](https://wolverinefx.net/guide/durability/efcore/migrations)
- [Wolverine: durable inbox and outbox messaging](https://wolverinefx.net/guide/durability/)
- [Wolverine: managing message storage](https://wolverinefx.net/guide/durability/managing)
- [MassTransit: transactional outbox](https://masstransit.io/documentation/patterns/transactional-outbox)
