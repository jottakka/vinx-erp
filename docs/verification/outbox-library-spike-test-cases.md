# Casos BDD — spike de biblioteca para outbox

## Escopo

Esta matriz verifica a decisão da spike #213, não um outbox em execução. A
única evidência concluída é comparativa: o scratch descartável em .NET 10
restaurou e compilou `WolverineFx.Postgresql` e
`WolverineFx.EntityFrameworkCore` 6.28.2, além de
`MassTransit.EntityFrameworkCore` 9.2.0, sem conflito de EF Core/Npgsql; os
contratos oficiais foram revisados. Nenhum pacote, runtime, worker, migration,
configuração, provider, endpoint ou UI foi introduzido no VINX.

| Classe BDD | Dado / quando / então | Evidência da spike | Prova operacional adiada |
|---|---|---|---|
| sucesso | Dada decisão de comparar bibliotecas de outbox PostgreSQL, quando o scratch é restaurado e compilado em .NET 10 e os contratos oficiais são revisados, então compatibilidade de referência fica registrada sem adoção no VINX. | Scratch .NET 10 compilou Wolverine 6.28.2 e MassTransit 9.2.0; contratos oficiais revisados. | #26 deve provar commit atômico entre estado de negócio, intenção e outbox. #48 deve provar infraestrutura genérica de processamento local por fake/contrato de provider, sem chamada real. |
| validação/erros | Dada falha de negócio ou timeout externo com resultado desconhecido, quando a futura entrega decidir destino, então timeout é classificado como `provider_ambiguous`, sem retry automático cego nem promessa de exactly-once externo. | Contratos oficiais revisados; nenhuma chamada a provider foi executada. | #26 deve provar rollback sem intenção/outbox. #48 deve tornar retry/DLQ observáveis e registrar estado seguro para reconciliação. #211B deve fazer reconciliação real do provider de convite e #28 a reconciliação Meta. |
| acesso | Dada futura mensagem persistida, quando precisar representar trabalho assíncrono, então contém somente identificadores internos opacos, tipo de evento e correlação mínima; não guarda PII, segredo ou payload bruto. | Contratos oficiais revisados; nenhum schema, mensagem, provider ou log foi criado. | #26 deve provar conteúdo mínimo em intenção/outbox sob migrations EF revisadas e grants mínimos do produtor no storage técnico (DML/USAGE, sem DDL nem ownership). #48 deve provar logs e DLQ sem payload sensível e os grants mínimos equivalentes do worker. |
| estado/exceções | Dada mutação de negócio futura, quando transação falhar ou for desfeita, então não pode sobrar intenção ou outbox; recursos técnicos não podem ser criados ou alterados em startup/runtime. | Contratos oficiais revisados; nenhuma transação, migration, startup ou recurso técnico foi executado. | #26 e #48 devem provar `AutoBuildMessageStorageOnStartup = AutoCreate.None`, `OverrideAutoCreateResources(AutoCreate.None)` e ausência de `UseEntityFrameworkCoreWolverineManagedMigrations()` em `FoundationDbContext`. Também devem provar que EF fica em VINX/Foundation e intenção própria, enquanto storage/envelopes Wolverine só mudam por deploy com identidade migradora aplicando `db-apply`, grants mínimos por identidade de runtime verificados com provas positivas e negativas e `db-assert` antes da liberação. |
| concorrência | Dadas duas instâncias futuras de worker, quando disputarem mesma mensagem, então não podem concluí-la duas vezes internamente; retry, lease e DLQ precisam manter decisão observável. | Contratos oficiais revisados; nenhum worker, lease, retry ou DLQ foi executado. | #48 deve provar lease, dois workers sem dupla conclusão interna, retry/DLQ observáveis e logs seguros com fake/contrato de provider. Runtime/worker não podem ter DDL nem ownership dos schemas técnicos Wolverine. |

## Critérios para adoção futura

- #26: demonstrar rollback sem intenção/outbox e commit atômico entre alteração
  de negócio, intenção e outbox, com migrations EF revisadas para schema
  VINX/Foundation e intenção própria. Deve provar que `FoundationDbContext`
  não usa `UseEntityFrameworkCoreWolverineManagedMigrations()`, que o produtor
  mantém `AutoBuildMessageStorageOnStartup = AutoCreate.None` e
  `OverrideAutoCreateResources(AutoCreate.None)` e que os grants do produtor no
  storage técnico são mínimos: DML/USAGE sem DDL nem ownership, com provas
  positivas e negativas.
- #48: demonstrar lease, dois workers sem dupla conclusão interna, retry e DLQ
  observáveis, logs sem payload sensível e contrato fake de provider, sem
  chamada real. Deve provar `AutoBuildMessageStorageOnStartup = AutoCreate.None`,
  `OverrideAutoCreateResources(AutoCreate.None)`, os privilégios mínimos de
  DML/USAGE/EXECUTE do worker, ausência de DDL/ownership em runtime/worker,
  cada qual com provas positivas e negativas, e liberação apenas depois de
  `db-assert` do storage técnico provisionado por identidade migradora/deploy.
- #211B, após #48: demonstrar adapter Supabase Admin de convite e reconciliação
  real de `provider_ambiguous`, vinculando somente `issuer+subject` confirmado,
  nunca email.
- #28, após a infraestrutura #48: demonstrar adapter Meta/WhatsApp, opt-in,
  templates e integração específica do webhook de canal assinado; reconciliar
  `provider_ambiguous` do Meta de modo idempotente e observável, por status,
  webhook ou identificador do provider quando disponíveis, sem retry cego. A
  #48 fica limitada ao contrato genérico de worker e estado de entrega.
- Deduplicação interna e outbox não equivalem a exactly-once externo.
