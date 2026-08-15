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
| `SUPABASE_AUTH_ISSUER` | API e MCP C# | Emissor JWT do Supabase Auth. |
| `SUPABASE_AUTH_AUDIENCE` | API e MCP C# | Audiência JWT aceita. |
| `SUPABASE_STORAGE_URL` | API C# | URL do adapter de Storage. |
| `SUPABASE_STORAGE_SERVICE_ROLE_KEY` | API C# | Credencial de serviço exclusiva do servidor. |
| `AllowedHosts` | MCP C# | Hostnames aceitos; a configuração versionada permite somente loopback e o deployment deve sobrescrever pelo hostname exato. |
| `ASPNETCORE_URLS` | API ou MCP C# | Binding HTTP local; use `http://127.0.0.1:5080` para API e `http://127.0.0.1:5081` para MCP. |

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
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost
```

A saúde da API fica em `http://127.0.0.1:5080/health`. A porta `54321` é o
gateway local do Supabase, não a API C#. Até #16, o web não chama essa API; a
base URL será configurada somente no cliente HTTP tipado dessa fatia.

## Executar o MCP C#

Com `SUPABASE_AUTH_ISSUER` e `SUPABASE_AUTH_AUDIENCE` configuradas, inicie a
sonda MCP local em outro terminal:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5081 \
  dotnet run --project src/Hosts/Vinx.Erp.McpHost
```

A saúde fica em `http://127.0.0.1:5081/health`; o transporte stateless fica em
`/mcp` e exige bearer token Supabase pré-obtido. Esta baseline não oferece
discovery OAuth MCP nem cliente browser: todo header `Origin` é rejeitado.
HTTP para o issuer/storage local só é aceito em loopback com o ambiente
`Development`; qualquer outro ambiente exige HTTPS.

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
