# PR3 — fronteira de revogação de membership

## Escopo entregue

- `Membership.Revoke(reason, actor, request)` faz soft-delete idempotente da
  membership e atualiza somente seu ciclo de vida/auditoria técnica. Não acessa
  nem altera sessões.
- `GrantRuntimeMembershipRevocation` concede ao runtime `UPDATE` somente em
  `deleted_at`, `deleted_by_user_id`, `deleted_reason`, `updated_by_user_id`,
  `updated_at`, `request_id` e `row_version` de `foundation.memberships`.
- Não há serviço, command/result, DI, endpoint, BDD, RLS policy, mapa ou
  catálogo novo.

## Estimativa e tamanho

- Previsto: 7 arquivos contados, 250–330 linhas manuais; mais migration,
  designer e snapshot gerados.
- Real: 6 arquivos manuais, aproximadamente 220 linhas manuais; migration e
  designer EF gerados. O snapshot do schema não mudou porque a alteração é só
  ACL, e `verify-foundation-database.sh` confirmou o dump idêntico.

## TDD

- RED domínio: `MembershipLifecycleTests` falhou com `CS1061` porque
  `Membership.Revoke` não existia.
- GREEN domínio: 3 de 3 testes passam: validação de razão, primeira transição
  e repetição idempotente.
- RED banco: matriz de grants esperava `UPDATE` e o teste de colunas falhou
  antes da migration.
- GREEN banco: 3 de 3 testes passam: allow-list das sete colunas, ausência de
  `INSERT`/`DELETE`/alteração de chaves e escopo RLS por empresa; downgrade
  remove todos os grants de `UPDATE` da migration.

## Verificações

- `dotnet build Vinx.Erp.sln --no-restore`: passou.
- Testes focados de unidade: 3 de 3 passaram.
- Testes focados de integração: 3 de 3 passaram.
- `./scripts/verify-foundation-database.sh`: passou.
- `./scripts/verify-ef-negative-guards.sh`: passou.
- `./scripts/verify-public.sh` foi iniciado; completou npm, lint, testes web,
  build .NET e database script. O handle encerrou antes de capturar o exit do
  loop final de testes, portanto não é registrado como gate completo.

## Pendência/risco

O runtime recebe grant por coluna, não `UPDATE` amplo. Em PostgreSQL isso faz
`has_table_privilege(..., 'UPDATE')` permanecer falso; os testes verificam os
privilégios de coluna diretamente para evitar interpretar isso como ausência
da permissão estreita. A execução integral do gate público deve ser repetida
no CI ou pelo integrador para evidência de saída final.
