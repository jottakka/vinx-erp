# Simplificação backend-first e remoção do runtime AI

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

Mesmo depois de retirar o coordenador, o projeto AI restante continuaria sendo
referenciado somente pelos próprios testes. A integração package-first sem host
também não entrega produto e fixa decisões antes de #135 e #138 terem provider
e API reais.

## Decisão

1. Manter as aprovações duráveis da Foundation e suas garantias de banco. Não
   haverá rollback de migração nem remoção de dados nesta fatia.
2. Remover todo o projeto `Vinx.Erp.AI`, as sondas, os testes sem consumidor e
   suas dependências diretas. Não manter módulo demonstrativo como promessa de
   arquitetura futura.
3. Reavaliar Microsoft Agent Framework, `IChatClient` e SDK oficial do provider
   na mesma fatia vertical de #135/#138 que registrar e usar o runtime.
4. Usar primeiro a plataforma ASP.NET Core. Dependências externas entram
   somente quando substituírem código atual ou atenderem um caso de uso já
   escolhido.

## Garantias preservadas

- As aprovações duráveis continuam isoladas por empresa, autorizadas e
  auditadas na Foundation, mas não são anunciadas como fluxo AI hospedado.
- Nenhum pacote, sonda ou API de agente permanece sem consumidor de produção.
- `vinx_ping` prova somente o transporte MCP e não importa runtime AI, provider
  ou acesso de domínio.

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
- Endpoints de provider exigem HTTPS. HTTP é aceito somente em loopback durante
  `Development`, preservando Supabase local sem permitir credencial em texto
  claro fora da máquina.
- A autenticação Supabase JWT é registrada uma vez na Foundation e reutilizada
  por `ApiHost` e `McpHost`. Issuer e audience continuam obrigatórios; claims
  não são remapeadas.
- O container privado de Storage deixa de ser configuração: permanece uma
  constante do servidor. O typed `HttpClient` recebe base URI e autenticação
  uma vez; `SupabaseFileStorage` recebe somente esse cliente e não repete
  opções, headers ou validação em cada chamada. Redirect automático fica
  desabilitado para impedir que o header privilegiado `apikey` atravesse para
  outra origem.
- `AddHealthChecks` e `MapHealthChecks` substituem o endpoint `/health` escrito
  à mão nos três hosts.
- `AddProblemDetails` e `UseExceptionHandler` cobrem somente exceções HTTP não
  tratadas da API. Erros de negócio preservam o corpo estável `{ "code": ... }`
  e 401/403 existentes não recebem corpo novo. No .NET 10, diagnósticos de
  exceções tratadas continuam habilitados explicitamente.
- O rate limiter nativo usa exatamente duas partições limitadas por host:
  `anonymous` e `authenticated`. A autenticação executa antes do limiter, então
  tráfego anônimo continua limitado sem esgotar o bucket autenticado. Nenhum
  subject, IP ou header controlado pelo cliente vira chave antes de #132
  materializar o usuário interno. Health fica fora da política; isolamento por
  usuário só entra depois do contexto confiável.

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
- O cliente Storage não segue redirect com a credencial de service role.

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
- publicar OpenAPI antes de #110 tipar statuses, schemas e bearer auth; um
  documento incompleto seria contrato falso para o frontend;
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

@security @backend
Scenario: Storage service credentials stay on the configured origin
  Given the Storage client carries a server-only service credential
  When the provider responds with a redirect
  Then the client does not follow that redirect

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
  Given requests reach a protected endpoint rapidly
  When the anonymous or authenticated host partition reaches its limit
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

## Tamanho final e limite do PR

O diff final contra `main` altera 61 caminhos: 50 de código/documentação e 11
lockfiles gerados. Sem lockfiles, são 1.708 adições e 4.786 remoções; os
lockfiles somam 1.096 adições e 822 remoções. O volume excede os limites usuais
de arquivos e linhas porque consolida a remoção do runtime AI não hospedado, a
baseline HTTP e a prova de transporte MCP. A exceção é explícita: o responsável
pediu a extensão na mesma PR 207. O saldo reduz código e dependências; ApiHost e
McpHost reutilizam a mesma autenticação e validação de opções, e `vinx_ping` não
acessa outro subsistema.
