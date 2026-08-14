# Relatório da Task 2 — protótipo funcional do Paguai

## Arquivos

- `apps/web/src/features/paguai/prototype/paguai-prototype-types.ts`
- `apps/web/src/features/paguai/prototype/paguai-prototype-fixtures.ts`
- `apps/web/src/features/paguai/prototype/use-paguai-prototype.ts`
- `apps/web/src/features/paguai/prototype/paguai-prototype-page.tsx`
- `apps/web/src/features/paguai/prototype/paguai-prototype-page.test.tsx`
- `apps/web/src/features/prototypes/prototype-index-page.tsx`
- `apps/web/src/shared/routing/app-paths.ts`
- `apps/web/src/app/app-route-manifest.tsx`

## Evidências TDD

- RED: `npm --prefix apps/web test -- --run src/features/paguai/prototype/paguai-prototype-page.test.tsx` falhou na resolução de `./paguai-prototype-page`, antes da implementação.
- GREEN: o mesmo teste passou com 4 testes; a suíte completa passou com 18 arquivos e 93 testes.
- Verificações adicionais: lint, build de produção e `git diff --check` passaram.

## Commit

`ac8dc81 feat(web): add functional Paguai prototype`

## Preocupações e limites

- O estado é somente local, sem timers, API ou persistência; recarregar descarta as alterações.
- O gate público completo chegou ao restore .NET e foi bloqueado por `SSH.NET 2025.1.0` com vulnerabilidade conhecida tratada como erro (`NU1903`), fora do escopo desta task.
- O build mantém o aviso existente de chunk JavaScript acima de 500 kB.
