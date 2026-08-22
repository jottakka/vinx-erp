# Revisão de main — segurança e simplicidade

## Estado atual

- Revisão do `main` (`7f6d745`, merge PR #273) numa worktree isolada
  `codex/review-main-seguranca-simplicidade` a partir de `main`.
- Escopo: segurança e simplicidade/manutenibilidade do backend .NET 10,
  frontend React e infra/config. Não executa build (SDK não disponível no
  ambiente de revisão); evidência por leitura estática.
- Dimensões verificadas: autenticação/autorização, segredos, injeção, SSRF,
  validação de entrada, antimalware/upload, cripto, RLS/banco, CI, dependências,
  duplicação, código morto e over-engineering.

## Conclusão geral

A base está em nível **muito alto** de higiene de segurança e simplicidade —
acima da média de projetos deste porte (~103.700 linhas de C#; 787 arquivos no
tree). A maioria das áreas críticas já pratica defesa em profundidade, fail-closed
e validação estrita. Os achados abaixo são refinamentos, não lacunas graves.

## Pontos fortes confirmados (sem ação necessária)

- **Autenticação JWT Supabase** (`SupabaseAuthenticationExtensions.cs`):
  validação de issuer/audience/assinatura/lifetime ativos, HTTPS obrigatório
  (loopback HTTP só em Development). Sem fragilidades.
- **Autorização por empresa (IDOR)**: toda entidade com escopo de tenant tem
  RLS com `FORCE ROW LEVEL SECURITY` + política `company_isolation` via
  `current_setting('app.company_id')`; a camada HTTP repete o escopo em
  `CapabilityEvaluator`/`AuthorizedCompanySetResolver`.
- **SSRF/AI egress** (`ProviderEgressPolicy.cs`): allowlist de hosts, rejeição
  de faixas privadas/loopback/link-local/CGNAT, defesa contra DNS rebinding e
  grafias numéricas não padrão (hex/octal/quad parcial). Excelente.
- **Webhook Meta** (`MetaWhatsAppWebhookVerifier.cs`): HMAC-SHA256 comparado em
  tempo constante (`CryptographicOperations.FixedTimeEquals`).
- **Antimalware/upload**: fail-closed — sem `ClamAvEndpoint` configura
  `BlockedMalwareScanner`; timeouts e limite de bytes validados on-start.
- **Composição do agente** (`AgentRunServiceCollectionExtensions.cs`): provider,
  endpoint, modelo e chave decididos **somente** na composição server-side; o
  request nunca escolhe endpoint/chave; sem provider o runner falha fechado.
- **Segredos**: nenhum segredo hardcoded encontrado; chaves só do ambiente,
  nunca logadas/ecoadas; `apikey` não atravessa redirects (`AllowAutoRedirect=false`).
- **Banco**: papéis `vinx_*` com `NOLOGIN`/`NOINHERIT`/`NOBYPASSRLS`/
  `NOSUPERUSER`; `REVOKE ALL ON SCHEMA public FROM PUBLIC`; separação migrator/
  runtime/worker/owners.
- **Entrada**: `JsonUnmappedMemberHandling.Disallow`, limite de 8 KiB no corpo
  do diretório de funcionários, rate limiting por assunto/IP, antiforgery,
  `Cache-Control: no-store` em endpoints autenticados.
- **Código morto**: sem TODO/FIXME/HACK; arquitetura validada por
  `Vinx.Erp.ArchitectureTests` (dependência de módulo, isolamento de fixture).

## Achados de segurança

| Severidade | Arquivo | Achado | Sugestão |
|---|---|---|---|
| — (verificado) | `src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/*.cs` | Reanálise: as políticas RLS **já** vivem em migrações EF auditáveis (ex.: `20260816012000_AddPlatformOrganizationProvisioning.cs`) e são cobertas por `RlsPolicyTests`; `infra/database/*.sql` contém apenas grants de papéis, fora do alcance do EF. Achado fechado sem mudança de código | — |
| Médio | `supabase/config.toml` | `minimum_password_length = 6`, `enable_signup = true`, `enable_confirmations = false` — defaults fracos de auth local | Elevar senha mínima p/ >=10, revisar confirmação de e-mail e restringir signup; confirmar que produção não herda esses defaults |
| Baixo | `.github/workflows/ci.yml` | Sem SAST/CodeQL, sem varredura de dependências, sem secret scanning | Adicionar CodeQL, Dependabot e push protection |
| Baixo | `.github/workflows/private-quality.yml` | `actions/checkout@v4` vs `@v5` no ci.yml — drift de versão | Unificar versões de actions |
| Baixo | `src/Hosts/Vinx.Erp.WorkerHost/Program.cs:107` | `NpgsqlDataSource.Create(string.Empty)` antes da validação on-start | Guardar criação atrás de validação (fail-fast explícito) |

## Achados de simplicidade / duplicação

| Severidade | Arquivo | Achado | Sugestão |
|---|---|---|---|
| Médio | `src/Hosts/Vinx.Erp.ApiHost/Program.cs` (849 linhas) | Dois contratos HTTP hand-rolled (`PlatformOrganizationHttpContract`, `EmployeeDirectoryHttpContract`) reimplementam allowlist+parsing já cobertos por `JsonUnmappedMemberHandling.Disallow` | Consolidar em mecanismo único de contrato estrito (atributo/validador reutilizável) |
| Médio | 3 validadores de URL | `MetaWhatsAppOptions.IsAllowedHttpsUrl` vs `WorkerHost.IsAllowedSupabaseUri` vs `SupabaseAuthenticationExtensions.IsAllowedProviderUri` com regras divergentes | Extrair `AllowedUriPolicy` único (HTTPS, sem UserInfo/Fragment, loopback-HTTP só em Development) e reutilizar |
| Baixo | `src/Hosts/*/Program.cs` | Setup repetido de logging/rate-limit/correlação entre ApiHost/McpHost/WorkerHost | Avaliar `AddVinxHostDefaults` compartilhado |
| Baixo | `apps/web/src/features/*/*-client.ts` | Sobra boillerplate de BFF por feature (parcialmente deduplicado em `d4fd07f`) | Reuso de `shared/http/bff-response.ts` em todos os clients |

## Observações (não bloqueantes)

- `dotnet` não está no PATH deste ambiente de revisão; build/testes não foram
  executados. Para revisão de fechamento, rodar `./scripts/verify-public.sh` e
  `dotnet list package --vulnerable` na worktree.
- `Vinx.Erp.Atendimento` tem apenas 2 arquivos (`AtendimentoCardService`,
  `AtendimentoCardContracts`) — verificar se o escopo está completo ou é fatia
  em construção (não é dívida; pode sinalizar WIP).

## Próximo passo sugerido

Tratar os Médias (contrato HTTP duplicado + validador de URI divergente + RLS
versionado + defaults fracos do `config.toml`) como PRs empilhados `codex/*`, um
por responsabilidade, seguindo `AGENTS.md` (fatia vertical única revisável,
<=25 arquivos, alvo 800 linhas manuais).

## Resolução aplicada nesta worktree

PR draft: https://github.com/jottakka/vinx-erp/pull/277

Cada ponto do handoff foi tratado como segue:

1. **Validadores de URL divergentes** — novo `src/Foundation/Vinx.Erp.Foundation/Configuration/AllowedUriPolicy.cs`
   (`IsAllowedHttp` com exceção loopback-dev e `IsAllowedHttps` sem exceção;
   ambos rejeitam UserInfo/Fragment). Substituiu as 4 variantes:
   `SupabaseAuthenticationExtensions` (Foundation), `WorkerHost`, `PlatformBootstrap`
   e `MetaWhatsAppOptions.IsAllowedHttpsUrl` (agora delegando). A validação de
   `SupabaseStorageOptions` no ApiHost também passou a usar a política.
2. **Contratos HTTP hand-rolled** — novo `src/Hosts/Vinx.Erp.ApiHost/Contracts/StrictJsonFieldSet.cs`
   (`TryReadObject`/`TryReadExact`), usado por `PlatformOrganizationHttpContract`,
   `EmployeeDirectoryHttpContract` e `AgentHttpContract`; a semântica (campos
   fechados, duplicatas e obrigatórios) permaneceu exata. Adicionado como
   `Compile Include` no `Vinx.Erp.Foundation.UnitTests.csproj`, que linka fontes do ApiHost.
3. **WorkerHost fail-fast** — `InvalidOperationException` explícito antes de
   `NpgsqlDataSource.Create`, eliminando o fallback com string vazia.
4. **Setup repetido dos hosts** — novo `VinxHostDefaultsExtensions.AddVinxHostDefaults()`
   (logging JSON + OpenTelemetry + health checks), usado por ApiHost, McpHost e
   WorkerHost; a referência a `OpenTelemetry.Extensions.Hosting` subiu para o
   Foundation e saiu dos csproj dos hosts. **Mudança de comportamento:** o
   ApiHost passa a emitir telemetria OpenTelemetry como os demais hosts.
5. **Clients BFF do frontend** — `shared/http/bff-response.ts` ganhou
   `fetchSameOriginBff`, `readAntiforgeryTokenBff`, `bffFailure`,
   `isAbortError` e o schema compartilhado de antiforgery. Os 4 clients
   (browser-session, foundation-bootstrap, employee-directory,
   platform-organization) perderam o scaffolding duplicado (`fetchSameOrigin`
   local, `getAntiforgery`, builder de falha e `isAbort`), preservando as
   semânticas específicas (ex.: `mayHaveCommitted` por client).
6. **Supabase local** — `supabase/config.toml`: `minimum_password_length=10`,
   `enable_signup=false` (onboarding é somente convite; fluxo de convite
   Supabase continua funcionando), `enable_confirmations=true` (e-mails caem
   no Inbucket local) e `secure_password_change=true`. **Produção fora deste
   repositório deve espelhar os mesmos valores.**
7. **CI** — novos `.github/workflows/codeql.yml` (C# + JS/TS; dispara em
   push para `main`, agendamento semanal e `workflow_dispatch` — **não** em
   `pull_request`: ver abaixo) e `.github/dependabot.yml` (nuget, npm e
   github-actions, agrupados e semanais); `private-quality.yml` alinhado às
   actions v5. **Secret scanning**: habilitar exige GitHub Advanced Security
   neste repositório privado (a API retornou 422 "not available"); ação do
   proprietário em Settings → Code security.
   **Estado real da CI (verificado 2026-08-22):** todos os workflows falham
   ao iniciar com a anotação "The job was not started because recent account
   payments have failed or your spending limit needs to be increased" —
   Actions está indisponível no repositório desde ~2026-08-09 (último run
   verde 2026-08-08). CodeQL em repositório privado exige, além do billing
   resolvido, GHAS/Code Security habilitado. Reabilitar o trigger de PR e
   fazer um smoke run manual (workflow_dispatch) quando ambos estiverem
   resolvidos em Settings → Billing & plans e Settings → Code security.
8. **RLS** — verificado como já versionado em migração EF (sem mudança).

### Verificação executada localmente

- `dotnet build`: 0 erros, 0 warnings.
- `dotnet test` Foundation.UnitTests (311, incl. 26 de AllowedUriPolicyTests
  adicionados na revisão), ArchitectureTests (21), BDD (5): todos verdes.
- Web: `vitest` 326/326 (incl. 9 casos diretos dos helpers de
  `bff-response`), `biome check` limpo, `tsc -b && vite build` ok.
- `dotnet format analyzers/style/whitespace --verify-no-changes`: conformes.
- Não executados aqui (exigem Docker/Banco): testes de integração e
  `verify-public.sh` completo — com o billing de Actions indisponível
  (item 7), estes ficam pendentes de execução local pelo revisor.

## Comando de start

```bash
git worktree add ../vinx-erp-review-seguranca-simplicidade \
  -b codex/review-main-seguranca-simplicidade main
```
