# Verificação pública

## Regra de execução

O CI executa o gate público completo em cada PR. Localmente, selecione testes
pelo comportamento alterado e seus consumidores transitivos; o caminho do diff
é apenas indício. Nunca descubra, execute ou relate `quality/private`.

| Fase | Verificação local |
|---|---|
| Investigação/RED | Um método, classe, cenário ou comando mínimo; sem gate completo ou Supabase por conveniência |
| Implementação/GREEN | Repetir o foco; depois o projeto afetado, ou a fatia estável definida para projeto misto e pesado |
| Correção | Somente contratos alterados; ampliar um projeto quando dependência compartilhada mudar |
| Fechamento | CI completo normal; gate local completo apenas com gatilho estratégico |

## Matriz de impacto

| Mudança | Fechamento local |
|---|---|
| Markdown, regras, runbook ou plano | `git diff --check`; Architecture somente se consumir o documento |
| React/cliente web | lint, formato, Vitest, build e no-direct-Supabase |
| Jornada/rota de browser | teste de componente e Playwright somente da jornada afetada |
| AI | AI Unit; AI Integration apenas para composição, contexto ou fronteira real |
| Foundation sem banco/HTTP | Foundation Unit |
| Foundation HTTP, persistência, módulo ou worker | filtros afetados; projeto Foundation Integration completo somente no CI ou com gatilho estratégico |
| McpHost/WorkerHost | Hosts Integration e consumidor direto necessário |
| BDD | cenário/tag afetado e Foundation BDD no fechamento |
| EF, migration, bootstrap SQL, RLS, grants ou snapshot | verificadores de banco/EF e filtros de integração afetados |
| Fixture, runner, pacote central, analisador, build ou script | contrato focado e um gate completo após congelamento |
| Storage/Supabase | testes sem Supabase primeiro; verificador Storage somente para Storage |

## Comandos por área

Em worktree nova ou mudança de `package-lock.json`, execute `npm ci` uma vez.
Antes do primeiro Playwright local, execute
`(cd apps/web && npx playwright install)`.

```bash
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm --workspace apps/web run test:e2e
npm run test:no-direct-supabase
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.AI.IntegrationTests/Vinx.Erp.AI.IntegrationTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'Category!=SupabaseStorage'
dotnet test tests/public/Vinx.Erp.Foundation.Bdd/Vinx.Erp.Foundation.Bdd.csproj --no-restore
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests/Vinx.Erp.Hosts.IntegrationTests.csproj --no-restore
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh
./scripts/verify-supabase-storage.sh
./scripts/verify-public.sh
```

Durante RED/GREEN, acrescente `--filter` com o nome completo do método ou classe
afetado. Não copie filtro genérico que possa esconder casos relevantes.
Para o projeto Foundation Integration, registre antes do GREEN a fatia estável:
todas as classes do módulo/contrato dono mais ciclo de vida e contratos
compartilhados aplicáveis. Após falha ou consumidor descoberto, a fatia só pode
ampliar.
Comandos `--no-restore` pressupõem `dotnet restore --locked-mode` já executado;
em worktree nova ou mudança de dependência, restaure uma vez antes dos testes.

## Gate completo e Docker

O gate local completo roda no máximo uma vez, após congelar o escopo, quando
muda infraestrutura compartilhada de teste/build, banco ou segurança
transversal; o diff cruza mais de um projeto público; surge falha cruzada; o CI
está indisponível; ou há pedido explícito. Mudança localizada usa testes focados
e o CI completo da PR. Correção somente documental após gate verde não o repete.

Antes de Docker pesado, resolva o contexto efetivo do verificador Storage e
atribua PID, PPID, tempo, comando, cwd/worktree e todos os recursos VINX:

```bash
vinx_docker_context="${DOCKER_CONTEXT:-}"
if [[ -z "$vinx_docker_context" && "$(uname -s)" == "Darwin" ]] && \
   docker context inspect desktop-linux >/dev/null 2>&1; then
  vinx_docker_context=desktop-linux
fi
if [[ -n "$vinx_docker_context" ]]; then
  export DOCKER_CONTEXT="$vinx_docker_context"
else
  unset DOCKER_CONTEXT
fi
for vinx_pid in $(pgrep -f 'dotnet test|testhost|verify-public|verify-foundation-database|verify-ef-negative-guards|verify-supabase-storage|supabase' || true); do
  ps -p "$vinx_pid" -o pid=,ppid=,etime=,command=
  if [[ "$(uname -s)" == "Darwin" ]]; then
    lsof -a -p "$vinx_pid" -d cwd -Fn 2>/dev/null
  else
    readlink "/proc/$vinx_pid/cwd" 2>/dev/null || true
  fi
done
for vinx_filter in \
  'label=org.testcontainers=true' \
  'name=vinx-foundation-verify-' \
  'name=vinx-foundation-ef-negative-guards-' \
  'label=com.docker.compose.project=vinx-erp-groundwork'; do
  docker ps --filter "$vinx_filter" \
    --format '{{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Labels}}'
done
```

Valor explícito de `DOCKER_CONTEXT` vence; sem valor, macOS prefere
`desktop-linux` disponível e Linux mantém o contexto padrão. Se o contexto ou o
dono permanecer ambíguo, espere em vez de iniciar ou limpar recursos.
Se outra worktree usa o daemon, espere ou coordene o dono; nunca interrompa
processo nem remova recurso alheio. Não execute o verificador Storage enquanto
a stack `vinx-erp-groundwork` preexistir ou seu dono for ambíguo, porque o script
a encerra no descarte. Depois, confirme que somente sua sessão desapareceu.

`verify-public.sh` instala dependências travadas, verifica web, restore/format/
build .NET, migrations, snapshot, guards EF e projetos públicos. O verificador
de banco usa `postgres:17.9-alpine` descartável, detecta mudança de modelo sem
migration, aplica o histórico e compara dump normalizado somente de schema com
o snapshot versionado e revisado antes dos testes. O guard negativo prova drift
controlado e `BrokenMigration` ausente do histórico. Storage é job separado e
chama somente
`verify-supabase-storage.sh`: não imprime configuração, limpa variáveis e
encerra somente a stack Supabase que iniciou; execute-o sem uma stack ativa.
Respeita `DOCKER_CONTEXT` explícito e reporta
falha de inicialização sem expor saída da CLI. Para inspeção local use
`DOCKER_CONTEXT=desktop-linux npx supabase status` sem mudar o contexto global.

Registre separadamente teste focado, gate local completo e CI; um não prova o
outro. A proteção desejada de branch privada pode não estar disponível no
GitHub Free. Nenhum agente envia ou faz merge em `main`; merge é operação
deliberada do responsável após revisão e verificações exigidas.
