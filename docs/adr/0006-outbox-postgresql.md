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
reavaliável, não como dependência instalada.

As guardas obrigatórias dessa adoção são:

- `AutoBuildMessageStorageOnStartup = AutoCreate.None` e
  `OverrideAutoCreateResources(AutoCreate.None)` impedem criação ou alteração
  automática de recursos no startup.
- Somente migrations EF revisadas criam ou alteram schema de negócio e de
  outbox. `FoundationDbContext`, suas migrations e seu histórico EF continuam
  fonte canônica desse schema.
- `UseEntityFrameworkCoreWolverineManagedMigrations()` não pode gerir
  `FoundationDbContext`; Weasel não será usado nesse contexto.
- A outbox contém somente identificadores internos opacos, tipo de evento e
  correlação mínima. O worker consulta dados necessários em fronteira estreita
  e autorizada.

## Limites de entrega e falha externa

A #26 implementará intenção e outbox transacionais e provará rollback sem
intenção/outbox e commit atômico. A #48 implementará worker, lease/concor­rência,
retry e DLQ observáveis, incluindo prova de que dois workers não concluem duas
vezes a mesma mensagem internamente. Adapter Supabase/Meta e aceite de convite
não pertencem a estas entregas.

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
- A operação precisa de migrations EF revisadas, observabilidade de estado e
  reconciliação para `provider_ambiguous`.
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
- [MassTransit: transactional outbox](https://masstransit.io/documentation/patterns/transactional-outbox)
