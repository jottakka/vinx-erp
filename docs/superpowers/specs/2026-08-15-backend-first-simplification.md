# Simplificação backend-first do runtime AI

## Contexto

O PR 205 adicionou duas coisas diferentes: aprovações duráveis na Foundation e
um coordenador em memória para retomar sessões do agente. As aprovações
duráveis são uma primitiva de backend com autorização, RLS, auditoria e
idempotência. Já o coordenador não está registrado em nenhum host, perde o
estado em reinício e implementa localmente timers, single-flight, shutdown,
retenção e entrega de decisões antes de existir uma API consumidora.

A prioridade atual é estabilizar APIs do backend para o frontend. Manter um
runtime de execução de escrita do agente sem host ou caso de uso real aumenta
o acoplamento e o custo de mudança sem entregar funcionalidade ao usuário.

## Decisão

1. Manter as aprovações duráveis da Foundation e suas garantias de banco. Não
   haverá rollback de migração nem remoção de dados nesta fatia.
2. Remover o coordenador em memória, o permit de entrega, o vínculo local de
   `RequestId` e os testes exclusivos desse runtime não registrado.
3. Manter o Microsoft Agent Framework como fronteira package-first, com
   aprovação automática desabilitada. A sonda de escrita continua produzindo
   uma pendência e não pode ser executada pela superfície pública.
4. Manter um feixe público opaco, limitado a criar sessão e executar turno
   textual. O agente bruto e a entrada de respostas de aprovação não serão
   expostos.
5. Reduzir `HarnessApprovalGate` à classificação fail-closed de propostas com
   contexto confiável do servidor. A retomada de sessão só volta quando houver
   endpoint e caso de uso reais.
6. Remover a dependência AI -> Foundation criada apenas pela coordenação. A
   autorização de capability continua pertencendo ao backend; a camada AI
   conserva apenas nome e risco das ferramentas de demonstração.
7. Usar primeiro a plataforma ASP.NET Core. Dependências externas entram
   somente quando substituírem código atual ou atenderem um caso de uso já
   escolhido.

## Garantias preservadas

- `DisableToolAutoApproval` permanece ativo e coberto por teste.
- Escrita sem decisão do servidor permanece pendente e sem efeito colateral.
- Ator e empresa usados pelo gate vêm de `VinxExecutionContext`, nunca do
  modelo.
- `CallId` vazio, ferramenta desconhecida e múltiplas solicitações são negados.
- As aprovações duráveis continuam isoladas por empresa, autorizadas e
  auditadas na Foundation, mas não são anunciadas como fluxo hospedado.

## Extensão aprovada na PR 207

Depois da remoção do runtime não hospedado, o responsável pediu que a mesma PR
entregue o primeiro baseline backend nativo e prove a fronteira MCP com o SDK
oficial. Foram consideradas três alternativas:

1. apenas trocar validações e health checks, deixando o `McpHost` vazio;
2. entregar o baseline nativo e uma ferramenta MCP estática, autenticada e sem
   dados de domínio;
3. antecipar OAuth, contexto confiável, catálogo e `whoAmI` das issues #133 e
   #125.

A alternativa 2 foi escolhida. Ela substitui código manual agora, estabelece o
transporte oficial sem fingir que o MCP V1 está pronto e não antecipa os casos
de uso multiempresa.

### Baseline ASP.NET Core

- `OptionsBuilder<T>`, `Validate` e `ValidateOnStart` passam a validar conexão
  Foundation, Supabase Auth e Supabase Storage. Chaves de configuração deixam
  de ser lidas e validadas manualmente no início do `Program.cs`.
- A autenticação Supabase JWT é registrada uma vez na Foundation e reutilizada
  por `ApiHost` e `McpHost`. Issuer e audience continuam obrigatórios; claims
  não são remapeadas.
- O container privado de Storage deixa de ser configuração: permanece uma
  constante do servidor. `SupabaseFileStorage` recebe opções já validadas e não
  repete a mesma validação em cada chamada.
- `AddHealthChecks` e `MapHealthChecks` substituem o endpoint `/health` escrito
  à mão nos três hosts.
- `AddProblemDetails` e `UseExceptionHandler` cobrem somente exceções HTTP não
  tratadas da API. Erros de negócio preservam o corpo estável `{ "code": ... }`
  e 401/403 existentes não recebem corpo novo. No .NET 10, diagnósticos de
  exceções tratadas continuam habilitados explicitamente.
- `Microsoft.AspNetCore.OpenApi` publica `/openapi/v1.json` somente em
  `Development`.
- O rate limiter nativo protege endpoints autenticados com uma única partição
  limitada por host. Isso evita cardinalidade controlada pelo cliente antes de
  #132 materializar o usuário interno. Health e OpenAPI ficam fora da política;
  isolamento por usuário só entra depois do contexto confiável.

### Baseline MCP

- `ModelContextProtocol.AspNetCore` 2.2.0 hospeda Streamable HTTP stateless em
  `/mcp`; não haverá implementação manual de JSON-RPC, SSE legado ou sessão.
- Todo o endpoint exige JWT Supabase válido e passa pelo rate limiter nativo.
- A única ferramenta é `vinx_ping`, sem argumentos, read-only, idempotente e
  fechada ao mundo externo. Ela retorna apenas um estado constante do host; não
  lê banco, claims, ator, empresa, capability, segredo nem serviço de domínio.
- `AllowedHosts` começa restrito a loopback e deve ser sobrescrito pelo hostname
  exato do deployment. Como não há cliente browser MCP nesta fatia, qualquer
  header `Origin` presente é negado; CORS não é usado como autorização.
- Esta prova não entrega OAuth/Protected Resource Metadata, contexto confiável,
  catálogo de domínio condicionado por capability nem `whoAmI`. Esses
  contratos permanecem em #133, #132 e #125 antes de qualquer ferramenta de
  negócio. O `tools/list` protocolar contém apenas `vinx_ping`. O acesso desta
  fatia é para clientes first-party com token Supabase pré-obtido, sem anunciar
  interoperabilidade OAuth MCP completa.

## Garantias da extensão

- Configuração inválida impede o host de iniciar por `OptionsValidationException`.
- Requisição MCP sem token não descobre nem executa ferramenta.
- Host não permitido ou requisição MCP com `Origin` presente é rejeitado antes
  da ferramenta.
- O transporte é stateless e não mantém identidade ou estado entre chamadas.
- Exceção inesperada da API produz Problem Details 500 sem mensagem interna e
  continua emitindo diagnóstico estruturado.
- Contratos atuais de erro e auditoria da Foundation não mudam.

## Fora do escopo

- criar endpoints de aprovação ou executar escritas do agente;
- alterar migrações ou o modelo de aprovação durável;
- integrar ferramenta MCP de domínio, provedor de IA ou frontend;
- adotar workflow engine antes de medir a necessidade no primeiro fluxo real;
- migrar para ASP.NET Core Identity: o VINX valida identidades Supabase e ainda
  precisa de membership multiempresa, capabilities, RLS e auditoria próprias;
- aplicar `AddValidation()` aos comandos de upload: hoje a validação ocorre
  depois de identidade/capability e gera auditoria; antecipá-la mudaria esse
  contrato e o corpo `{ "code": ... }`;
- adicionar retry/resilience ao cliente Storage: POST e DELETE não podem ser
  repetidos por política genérica;
- impor timeout global: os SLAs das APIs ainda não foram definidos e Streamable
  HTTP pode manter a resposta aberta durante a chamada;
- implementar OAuth MCP completo, contexto multiempresa, `whoAmI`, catálogo de
  domínio, provider, outbox ou workflow.

## BDD da extensão

```gherkin
@success @backend
Scenario: Hosts start with validated configuration and native health checks
  Given all required backend options are valid
  When each host starts and health is requested
  Then the native health endpoint returns Healthy

@validation @backend
Scenario: Invalid backend configuration fails before serving traffic
  Given a required connection, issuer, audience, storage URL or key is invalid
  When the affected host starts
  Then options validation prevents startup with no secret in the error

@access @mcp
Scenario: Unauthenticated callers cannot use the MCP transport
  Given no valid Supabase bearer token
  When the caller posts to the MCP endpoint
  Then the host returns 401 and discloses no tool result

@state @mcp
Scenario: Authenticated ping calls carry no session state
  Given a valid bearer token
  When the client discovers and calls vinx_ping more than once
  Then each call returns the same constant safe result
  And the server creates no VINX session or domain state

@concurrency @security
Scenario: Excess protected requests are bounded without unbounded partitions
  Given authenticated requests reach the protected host rapidly
  When the single host partition reaches its limit
  Then excess requests receive 429
  And health remains available outside the policy
```

## Ordem de entrega

A sequência durável fica em
`docs/handoffs/2026-08-15-backend-api-fe-priorities.md`. Em resumo: baseline
ASP.NET Core, identidade/contexto de empresa e contratos HTTP primeiro; APIs
verticais depois; frontend pode avançar em paralelo quando esses contratos
estiverem estáveis; MCP e provedores reutilizam os mesmos casos de uso por
último.

## Estimativa e limite do PR

A remoção original altera 25 caminhos, adiciona menos de 700 linhas manuais e
remove mais de 3.000. A extensão deve alterar de 20 a 28 caminhos adicionais e
adicionar de 450 a 750 linhas manuais; lockfiles gerados serão reportados à
parte. O conjunto completo excede os limites usuais de arquivos e linhas e une
baseline HTTP com transporte MCP. A exceção é explícita: o responsável pediu a
extensão na mesma PR 207. O vínculo é concreto porque ambos os hosts reutilizam
a mesma autenticação e validação de opções, e a ferramenta MCP não acessa outro
subsistema.
