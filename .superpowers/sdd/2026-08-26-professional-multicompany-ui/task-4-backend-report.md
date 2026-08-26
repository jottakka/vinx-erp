# Task 4 — evidência do proxy web

## RED/GREEN

- RED (2026-08-26): `npm --workspace apps/web run test -- --run src/app/vite-api-proxy.test.ts` falhou em 1 de 15 testes porque não existia a entrada exata `^/atendimento/assignment-options(?:\\?|$)`.
- GREEN (2026-08-26): o mesmo comando passou com 1 arquivo e 15 testes.

## Alteração e verificações

- `apps/web/src/app/vite-api-proxy.test.ts` agora exige o proxy exato de `assignment-options`, confirma o destino ApiHost, rejeita a chave ampla `/atendimento` e mantém `/atendimento` fora de todos os contextos de proxy.
- `apps/web/vite.config.ts` adiciona somente `^/atendimento/assignment-options(?:\\?|$)`, preservando o proxy existente de cards e as rotas SPA.
- Biome focado nos dois arquivos: passou.
- `git diff --check`: passou.

## Concern

Nenhuma preocupação adicional nesta fatia. Não foram executados testes backend, Docker ou o gate público completo.
