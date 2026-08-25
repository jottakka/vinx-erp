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
| `FOUNDATION_WORKER_CONNECTION` | Worker C# | Conexão de runtime exclusiva da role `vinx_worker`, no mesmo banco físico da API. |
| `FOUNDATION_MIGRATOR_CONNECTION` | Ferramentas EF, bootstrap de operador e deploy do outbox | Conexão de migração, separada do runtime. |
| `SUPABASE_AUTH_SERVICE_ROLE_KEY` | Bootstrap de operador e Worker C# | Credencial Admin Auth exclusiva do servidor; nunca usar no browser. |
| `PLATFORM_OPERATOR_SUBJECT` | Bootstrap de operador C# | Subject já confirmado no Supabase Auth para o único operador inicial. |
| `PLATFORM_OPERATOR_DISPLAY_NAME` | Bootstrap de operador C# | Nome de apresentação validado no deployment; não participa da autorização. |
| `REPROVISION_IDENTITY_USER_ID` | Recuperação offline de identidade | ID interno exato da mesma pessoa; nunca selecionar por e-mail. |
| `REPROVISION_IDENTITY_EXPECTED_EXTERNAL_IDENTITY_ID` | Recuperação offline de identidade | ID exato do vínculo externo que pode trocar de subject. |
| `REPROVISION_IDENTITY_EXPECTED_OLD_SUBJECT` | Recuperação offline de identidade | Subject que deve estar ausente no Admin Auth antes da recuperação. |
| `REPROVISION_IDENTITY_NEW_SUBJECT` | Recuperação offline de identidade | Subject novo, confirmado e não anônimo no Admin Auth. |
| `SUPABASE_AUTH_ISSUER` | API, MCP e Worker C# | Emissor JWT do Supabase Auth. |
| `SUPABASE_AUTH_AUDIENCE` | API, MCP e Worker C# | Audiência JWT aceita. |
| `SUPABASE_INVITE_REDIRECT_URL` | Worker C# | URL HTTPS permitida pelo Supabase para o convite inicial; o aceite ainda não é publicado nesta fatia. |
| `SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS` | Worker C# (opcional) | Lista de origens absolutas separadas por `;` que restringe a origem do redirect; ausente mantém a validação de formato; malformada rejeita o startup. |
| `SUPABASE_AUTH_PUBLISHABLE_KEY` | API C# | Configuração de host somente servidor para login BFF; nunca expor como `VITE_*` nem usar para chamada direta do browser ao Supabase. |
| `SUPABASE_STORAGE_URL` | API C# | URL do adapter de Storage. |
| `SUPABASE_STORAGE_SERVICE_ROLE_KEY` | API C# | Credencial de serviço exclusiva do servidor. |
| `VINX_API_ORIGIN` | Processo Vite de desenvolvimento | Origem local exata do ApiHost para o proxy de `/agent/runs`, `/atendimento/cards`, `/foundation`, `/notifications` e `/paguai/requests`; aceitar somente `http://127.0.0.1:<porta>`, nunca prefixo `VITE_*` nem URL de provider. |
| `BROWSER_SESSION_DATA_PROTECTION_KEY_DIRECTORY` | API C# em produção | Diretório persistente, durável e compartilhado por todas as réplicas do host; acesso restrito ao processo. |
| `BROWSER_SESSION_DATA_PROTECTION_APPLICATION_NAME` | API C# em produção | Nome estável, isolado por aplicação e ambiente; não compartilhar com outro sistema. |
| `BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PATH` | API C# em produção | Certificado disponível ao host para criptografar o key ring durável. |
| `BROWSER_SESSION_DATA_PROTECTION_CERTIFICATE_PASSWORD` | API C# em produção | Senha do certificado, mantida em configuração secreta e criptografada no store de deployment. |
| `AllowedHosts` | MCP C# | Hostnames aceitos; a configuração versionada permite somente loopback e o deployment deve sobrescrever pelo hostname exato. |
| `ASPNETCORE_URLS` | API ou MCP C# | Binding HTTP local; use `http://127.0.0.1:5080` para API e `http://127.0.0.1:5081` para MCP. |

O frontend não define variável de provider. Para desenvolvimento local, o
processo Vite pode receber `VINX_API_ORIGIN=http://127.0.0.1:5080`; ela é lida
somente por `vite.config.ts` e encaminha apenas `/agent/runs`,
`/atendimento/cards`, `/foundation`, `/notifications` e `/paguai/requests` sem
trocar a origem vista pelo browser. `/atendimento` e `/paguai` permanecem rotas
SPA válidas para acesso direto e refresh. Nenhum `VITE_*` deve conter URL ou chave do
Supabase, conexão PostgreSQL ou credencial de serviço. Em particular, não crie
`VITE_SUPABASE_*`: o browser usa apenas rotas BFF da mesma origem e nunca chama
Supabase diretamente. Preview e produção não usam esse proxy; o ingress TLS
precisa encaminhar esses cinco contextos de API ao ApiHost sem redirect, CORS amplo ou
perda de `Set-Cookie` e `X-Correlation-ID`.
Se uma variável, contrato ou serviço necessário não estiver confirmado, registre
o bloqueio na issue correspondente. Não invente valor, crie segredo de produção
ou libere acesso direto como atalho.

Em produção, as quatro configurações de Data Protection acima são obrigatórias
para sessão BFF. O key ring deve sobreviver a restart e deployment, ser comum
às réplicas do mesmo host, permanecer isolado de outras aplicações e ser
criptografado pelo certificado. Não copie valores para arquivo versionado,
browser ou log.

## Executar a API C#

Para inspeção manual com banco descartável e identidades de teste, use o fluxo
test-only de `docs/runbooks/real-e2e-fixture.md`. Um comando sobe PostgreSQL,
ApiHost e Vite sem senha, token ou Supabase local:

```bash
npm run dev:real-e2e -- --profile maria
```

O fluxo aceita também `beatriz`, `joao` e `clara`; encerre com `Ctrl+C` e
aguarde o cleanup antes de iniciar outro profile.

Com as variáveis de runtime da API já configuradas no processo pelo
fluxo seguro do time, inicie a API de domínio em outro terminal:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost
```

A saúde da API fica em `http://127.0.0.1:5080/health`. A porta `54321` é o
gateway local do Supabase, não a API C#. O web chama caminhos relativos de
`/agent/runs`, `/atendimento/cards`, `/foundation`, `/notifications` e
`/paguai/requests`; em desenvolvimento, somente o proxy Vite usa
`VINX_API_ORIGIN` para alcançar esta porta. Não configure base URL absoluta no
cliente browser.

## Executar o agente (`POST /agent/runs`)

A execução supervisionada do agente fica no mesmo ApiHost e é composta
server-side pela seção `AgentRun` da configuração. Sem chave de provider a
superfície sobe normalmente e falha fechado: um `POST /agent/runs` autenticado
retorna `503` com `{"code":"provider_not_configured"}` e nada persiste —
o `requestId` pode retentar quando o provider for configurado. Esse é o
estado esperado para desenvolvimento local sem custos nem chave.

Com uma chave real obtida pelo fluxo seguro do time, exporte no processo antes
de `dotnet run` (a chave vai somente ao cofre de configuração do host; nunca a
`VITE_*`, arquivo versionado ou log):

```bash
AgentRun__Provider=openai_compatible \
AgentRun__Endpoint=https://provider.example/v1 \
AgentRun__Model=modelo-aprovado \
AgentRun__ApiKey=$AGENT_RUN_API_KEY \
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost
```

O provider aprovado é `openai_compatible` ou `deepseek`; o endpoint precisa ser
HTTPS absoluto — a política de egress do módulo rejeita loopback, então testar
com provider real em máquina local exige um proxy com terminação TLS — e
configuração inválida derruba o host na partida. A jornada
`read_only_summarize_v1` exige sessão
autenticada, `companyId` de uma empresa com membership e a capability `ai.read`
em escopo Company. A matriz completa de desfechos (replay por `requestId`,
`requires_confirmation` opaco, timeout 504 `agent_timeout`) está em
`docs/verification/issue-138-agent-api-test-cases.md`.

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

## Bootstrap do operador de plataforma

Depois de aplicar o bootstrap de roles e as migrações, configure no ambiente
de deployment a conexão migradora, as variáveis de Auth e o subject do
operador já existente no Supabase. O comando não aceita argumentos:

    dotnet run --project src/Tools/Vinx.Erp.PlatformBootstrap

Ele consulta o usuário pelo Admin Auth somente no servidor e devolve uma
categoria estável: `created`, `already_configured`, `conflict`,
`provider_rejected`, `unavailable` ou `configuration_invalid`. A repetição
do mesmo subject é segura; outro subject exige investigação operacional, sem
alterar dados diretamente no banco.

## Recuperar uma identidade reprovisionada

Use somente após confirmar, pelo procedimento operacional aprovado, que é a
mesma pessoa e que o subject antigo foi removido do provedor. Com as quatro
variáveis `REPROVISION_IDENTITY_*`, `FOUNDATION_MIGRATOR_CONNECTION`,
`SUPABASE_AUTH_ISSUER`, `SUPABASE_AUTH_AUDIENCE` e a credencial Admin Auth já
presentes no ambiente seguro, execute sem argumentos adicionais:

```bash
dotnet run --project src/Tools/Vinx.Erp.PlatformBootstrap -- reprovision-identity
```

Sucesso imprime somente `reprovisioned` ou `already_configured`. Antes de
liberar acesso, confira o login com o novo subject e a nova sessão; sessões
anteriores devem exigir nova autenticação. `provider_not_deleted`,
`provider_rejected`, `email_mismatch`, `tuple_mismatch`, `unavailable` e
`configuration_invalid` não autorizam retentativa cega: preserve o estado,
investigue pelo canal seguro e escale para a operação de identidade. Não rode
SQL direto, não altere a identidade por e-mail e não copie IDs, e-mails,
payloads ou segredos para tickets ou logs.

## Provisionar o storage técnico do outbox e worker

Depois das migrations Foundation e antes de iniciar o ApiHost produtor com a
role de runtime, execute no diretório raiz:

```bash
./scripts/setup-wolverine-storage.sh
```

O comando exige `FOUNDATION_MIGRATOR_CONNECTION`, sem imprimi-la, e executa na
ordem: `db-apply` do ApiHost, `db-apply` do WorkerHost, grants mínimos e
`db-assert` dos dois hosts. Durante o deploy, a conexão migradora é fornecida
somente aos comandos como conexão de cada host para que Foundation, produtor e
worker apontem ao mesmo banco físico.

Isso não concede ownership ao runtime: o produtor usa `vinx_wolverine`, o
worker usa `vinx_wolverine_worker` e ambos compartilham somente
`vinx_wolverine_queues`. Nenhum host cria ou altera esses recursos no startup.

## Executar o WorkerHost

Após provisionar o storage, inicie o worker com uma conexão de runtime da role
`vinx_worker`:

```bash
dotnet run --project src/Hosts/Vinx.Erp.WorkerHost
```

O preflight confere `session_user` e `current_user` antes de iniciar o
listener; outra role falha fechada. O worker ingressa o convite inicial e, no
vencedor da tentativa, usa o Admin Auth do Supabase exclusivamente no servidor.
Sucesso fica `pending_acceptance`; rejeição definitiva fica `provider_failed`;
resultado desconhecido fica `provider_ambiguous` sem novo envio automático.
Falhas
permanentes vão para a DLQ do schema do worker com corpo e headers sanitizados;
investigue pelo ID opaco e pela correlação, não por payload bruto.

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

Selecione testes e gates pela matriz de
[`public-verification.md`](public-verification.md). O CI é o gate completo normal
da PR; execute `./scripts/verify-public.sh` local somente uma vez, após congelar
o escopo, quando um gatilho estratégico listado naquele runbook se aplicar.

## Diagnóstico e parada

O primeiro `npx supabase start` baixa imagens Docker. Em timeout transitório,
aguarde e consulte `DOCKER_CONTEXT=desktop-linux npx supabase status`.
`--ignore-health-check` serve apenas para diagnóstico. Se Docker Desktop e
Colima coexistirem, confira antes `docker context show`.

Os testes `Vinx.Erp.Foundation.IntegrationTests` permitem no máximo duas
collections xUnit em paralelo por processo. Worktrees simultâneas permanecem
processos independentes e multiplicam esse teto; não execute gates redundantes
sem necessidade.

Docker Desktop e Colima mantêm VMs distintas. Se `docker context show` retornar
`desktop-linux`, consulte `colima status`. Execute `colima stop` somente depois
de confirmar que nenhuma outra worktree ou projeto depende dessa instância; o
repositório não para runtimes globais automaticamente.

```bash
DOCKER_CONTEXT=desktop-linux npx supabase stop
```

Use `DOCKER_CONTEXT=desktop-linux npx supabase stop --no-backup` somente quando
o descarte do banco local for intencional.
