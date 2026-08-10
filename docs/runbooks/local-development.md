# Desenvolvimento local

## Escopo e fronteiras

Este guia prepara o ambiente para as fatias já aprovadas; não cria um segundo
scaffold, serviço de produção ou segredo de teste. A aplicação web fica em
`apps/web` e usa React, TypeScript, Vite, MUI, TanStack Query, React Hook Form,
Zod, Vitest, Testing Library, MSW e Playwright.

| Parte | Responsabilidade local | Nunca faz |
|---|---|---|
| Browser | Renderiza a interface e chama a API de domínio C#. | Acessar Supabase, Storage ou PostgreSQL diretamente. |
| API C# | Autentica, autoriza e aplica contratos de domínio; usa boundaries de provider. | Expor chaves de serviço ao browser. |
| Supabase local | Oferece Auth, Storage e runtime local aos adapters do servidor. | Ser cliente de dados ou administração pelo browser. |
| PostgreSQL | Sustenta o runtime, migrações e contêineres descartáveis de teste. | Receber conexão direta do browser. |

O web atual contém somente rota de saúde. O cliente HTTP tipado e o contrato
com a API entram em [#16](https://github.com/jottakka/vinx-erp/issues/16); o
bootstrap de pessoa, memberships e flags entra em
[#90](https://github.com/jottakka/vinx-erp/issues/90). A autorização e a
leitura multiempresa são entregas de Foundation em #19, #20 e #68, não
decisões do browser.

## Pré-requisitos

- Node.js `22.23.2` (arquivo `.nvmrc`).
- .NET SDK `10.0.302` (arquivo `global.json`).
- Docker Desktop em execução. Em macOS, os comandos Supabase abaixo usam o
  contexto `desktop-linux` sem alterar o contexto global.
- Não instale Supabase CLI globalmente: ele é dependência do projeto.

Valide ferramentas e instale dependências travadas a partir da raiz:

```bash
npm ci
npm run tools:check
dotnet tool restore
dotnet restore --locked-mode
```

## Serviços locais

```bash
DOCKER_CONTEXT=desktop-linux npx supabase start
DOCKER_CONTEXT=desktop-linux npx supabase status
```

Com a configuração padrão, os endereços locais são:

- Gateway do Supabase: `http://127.0.0.1:54321`
- Studio: `http://127.0.0.1:54323`
- Mailpit: `http://127.0.0.1:54324`
- PostgreSQL: porta `54322`

O Compose do repositório declara apenas a rede compartilhada `vinx-erp`. Não
inicie um segundo PostgreSQL, Auth ou Storage paralelo ao Supabase local.

## Variáveis locais esperadas

Guarde valores reais fora do Git e fora do browser. `.env.local` é ignorado,
mas não deve ser tratado como mecanismo de distribuição de segredos. Obtenha
as configurações locais pelo fluxo seguro do time; `supabase status` mostra
endereços e credenciais geradas apenas para uso local.

| Variável | Processo que a usa | Uso |
|---|---|---|
| `FOUNDATION_RUNTIME_CONNECTION` | API C# | Conexão de runtime com privilégios mínimos. |
| `FOUNDATION_MIGRATOR_CONNECTION` | Ferramentas EF | Conexão de migração, separada do runtime. |
| `SUPABASE_AUTH_ISSUER` | API C# | Emissor JWT do Supabase Auth. |
| `SUPABASE_AUTH_AUDIENCE` | API C# | Audiência JWT aceita. |
| `SUPABASE_STORAGE_URL` | API C# | URL do adapter de Storage. |
| `SUPABASE_STORAGE_SERVICE_ROLE_KEY` | API C# | Credencial de serviço exclusiva do servidor. |
| `ASPNETCORE_URLS` | API C# | Porta HTTP local; use `http://127.0.0.1:5080` neste guia. |

O frontend V1 não define variável de provider. Quando #16 introduzir uma base
URL da API, ela será documentada junto do cliente tipado; nenhum `VITE_*` deve
conter URL ou chave do Supabase, conexão PostgreSQL ou credencial de serviço.
Se uma variável, contrato ou serviço necessário não estiver confirmado, registre
o bloqueio na issue correspondente. Não invente valor, crie segredo de produção
ou libere acesso direto como atalho.

## Executar a API C#

Com as cinco variáveis de runtime da API já configuradas no processo pelo
fluxo seguro do time, inicie a API de domínio em outro terminal:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost
```

A saúde da API fica em `http://127.0.0.1:5080/health`. A porta `54321` é o
gateway local do Supabase, não a API C#. Até #16, o web não chama essa API; a
base URL será configurada somente no cliente HTTP tipado dessa fatia.

## Executar e testar o web

```bash
(cd apps/web && npx playwright install)
npm --workspace apps/web run dev
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm --workspace apps/web run test:e2e
```

O primeiro uso de Playwright pode exigir a instalação dos navegadores pelo
próprio Playwright. O teste E2E sobe o preview local automaticamente. No fim
de cada funcionalidade, os BDDs de sucesso e erro em inglês são exercitados
com Playwright; a orquestração final usa `gpt-5.6-luna` conforme a definição de
pronto da issue.

## Online-only e conectividade

A V1 funciona somente online. Não adicione PWA, service worker, cache de
resposta autenticada, fila de escrita nem replay automático. A interface pode
mostrar indisponibilidade quando sinais do navegador e falhas reais da API a
confirmarem. Depois de reconectar, ela revalida sessão, memberships,
capabilities e queries antes de atualizar a tela.

## Verificações Foundation e Storage

```bash
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
./scripts/verify-supabase-storage.sh
```

Os dois primeiros comandos usam PostgreSQL descartável e não requerem a
credencial de runtime. O último inicia e encerra a stack local, verifica o
bucket privado `vinx-foundation-private` e não imprime credenciais.

Para uma verificação pública completa antes de um PR de comportamento:

```bash
./scripts/verify-public.sh
```

## Diagnóstico e parada

O primeiro `npx supabase start` baixa imagens Docker. Em timeout transitório,
aguarde e consulte `DOCKER_CONTEXT=desktop-linux npx supabase status`.
`--ignore-health-check` serve apenas para diagnóstico. Se Docker Desktop e
Colima coexistirem, confira antes `docker context show`.

```bash
DOCKER_CONTEXT=desktop-linux npx supabase stop
```

Use `DOCKER_CONTEXT=desktop-linux npx supabase stop --no-backup` somente quando
o descarte do banco local for intencional.
# Local development

## Prerequisites

Install Node.js, .NET and Docker Desktop at the versions recorded in the
repository handoff. The Supabase CLI is a project dependency; do not install it
globally.

## Start local services

```bash
npm install
DOCKER_CONTEXT=desktop-linux npx supabase start
DOCKER_CONTEXT=desktop-linux npx supabase status
```

With the default local configuration, the main service URLs are:

- API: `http://127.0.0.1:54321`
- Studio: `http://127.0.0.1:54323`
- Mailpit: `http://127.0.0.1:54324`

The local PostgreSQL port is `54322`; other generated credentials and service
URLs are displayed by `DOCKER_CONTEXT=desktop-linux npx supabase status`.
Keep them local. `.env.local` is ignored by Git and must never be committed.

The repository's Compose file declares only the shared `vinx-erp` network. It
does not start a second PostgreSQL, Auth or Storage service alongside
Supabase.

## Verify private Storage provisioning

Run the dedicated local-provider gate from the repository root:

```bash
./scripts/verify-supabase-storage.sh
```

The command starts and stops the project-local Supabase stack itself. It keeps
the generated service-role key in process environment only, verifies the fixed
`vinx-foundation-private` bucket is private and does not expose credentials to
browser code.

## Verify Foundation migrations and schema

The Foundation migration checks use a fresh `postgres:17.9-alpine` container
and do not use the local Supabase database or require a runtime database
credential:

```bash
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
```

To deliberately refresh the reviewed schema artifact after a migration change,
run the explicit write command and inspect its diff:

```bash
./scripts/generate-foundation-schema-snapshot.sh --write
git diff -- docs/database/foundation-schema.snapshot.sql
```

## Troubleshooting startup

The first `npx supabase start` downloads the local Docker images and can take
some time. If a transient health-check timeout occurs, wait for the services
to stabilize, then check them with
`DOCKER_CONTEXT=desktop-linux npx supabase status`. Use
`--ignore-health-check` only for diagnosis, never as the normal start command.

Vinx ERP assumes Docker Desktop. If Docker Desktop and another runtime such as
Colima coexist, first confirm the intended runtime with `docker context show`.
Every Supabase command in this runbook explicitly selects Docker Desktop for
that process; it does not change the user's global Docker context.

## Stop local services

```bash
DOCKER_CONTEXT=desktop-linux npx supabase stop
```

Use `DOCKER_CONTEXT=desktop-linux npx supabase stop --no-backup` only when
discarding local database state is intentional.
