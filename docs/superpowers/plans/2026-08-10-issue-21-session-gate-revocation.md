# Gate de sessão e revogação global própria — plano de implementação

> **Para agentes:** subskill obrigatório: superpowers:subagent-driven-development. Cada tarefa usa TDD, relatório persistente e revisão independente.

**Objetivo:** concluir a #21 fazendo toda rota protegida validar a sessão interna e permitindo que a própria pessoa revogue todas as suas sessões.

**Arquitetura:** depois de UseAuthentication, um middleware identifica apenas endpoints protegidos, resolve a identidade já validada, admite ou verifica a sessão EF e grava ActorContext tipado no HttpContext. POST /foundation/identity/sessions/revoke-all usa esse ator, chama função SQL estreita e grava auditoria global na mesma transação.

**Tecnologia:** .NET 10, Minimal APIs, EF Core/Npgsql/PostgreSQL, xUnit e WebApplicationFactory.

## Restrições globais

- Base: origin/main em d6267db; esta PR usa codex/issue-21-session-gate-plan. A execução parte dela em codex/issue-21-session-gate-revocation.
- Esta PR entrega somente gate HTTP e revoke-all próprio da #21. #79, #80, #81, #18, #19 e #20 não entram.
- Pipeline explícito: UseRouting, UseAuthentication, gate interno, UseAuthorization. Health e AllowAnonymous não consultam identidade ou sessão.
- Rota: POST /foundation/identity/sessions/revoke-all, sem corpo, userId ou companyId. Revoked e NoChange devolvem 204 No Content.
- Admitted e Active permitem o handler. Prova inválida, identidade desconhecida e Denied devolvem 401. Falha de banco ou auditoria devolve 503 com identity_gate_unavailable.
- iat não é evidência suficiente. Somente autenticação efetiva estritamente posterior a sessions_valid_after entra após revogação.
- A função PostgreSQL usa SECURITY DEFINER, search_path fixo, relógio do banco, PUBLIC revogado e privilégio mínimo. vinx_runtime não recebe UPDATE direto em users nem em user_sessions.
- Auditoria usa AuditScope.Global, ação identity_sessions_revoked, recurso user, ator corrente, correlação do servidor e metadata somente com contagem. Nunca registrar JWT, refresh token, e-mail, IP, User-Agent, AMR bruto ou provider_session_id.
- Negação antes de existir pessoa interna não ganha auditoria: audit_events exige ator interno e criar ator sintético é inseguro.
- Todos os testes ficam em tests/public. Nunca ler, listar ou executar quality/private.
- Antes do push: testes focados, revisão independente, gate público completo uma vez e git diff --check.

## BDD e tamanho

| Classe | Dado / Quando / Então | Teste previsto |
| --- | --- | --- |
| Sucesso | Dada prova válida e identidade conhecida, quando rota protegida é chamada, então gate admite sessão, cria ator e handler executa. | IdentityGateEndpointTests |
| Revogação | Dadas sessões próprias ativas, quando revoke-all é chamado, então barreira avança, sessões conhecidas são revogadas, uma auditoria é gravada e token antigo recebe 401. | serviço e endpoint |
| Validação | Dada prova ausente ou inválida, quando rota protegida é chamada, então recebe 401, sem sessão ou handler. | IdentityGateEndpointTests |
| Acesso | Dado cliente sem autenticação, quando acessa rota protegida ou revoke-all, então recebe 401; health permanece anônimo. Tenant incorreto é N/A: operação não aceita empresa. | IdentityGateEndpointTests |
| Estado/exceção | Dada sessão revogada, barreira igual/anterior ou falha de banco/auditoria, quando gate ou revogação executa, então recebe 401 ou 503 sem mutação parcial. | serviço e endpoint |
| Concorrência | Dadas admissão e duas revogações simultâneas, quando disputam o mesmo usuário, então lock serializa, somente uma revogação altera estado/audita e a NoChange preserva a barreira que nega prova antiga ainda não observada. | SelfSessionRevocationServiceTests |

Estimativa: 17–21 arquivos totais, 680–790 linhas manuais. Migration, designer e snapshot gerados são reportados separadamente. Banco e API ficam na mesma fatia porque endpoint só é seguro com gate, lock e auditoria atômicos.

---

### Tarefa 1: Revogar sessões próprias de forma atômica no banco

**Arquivos:**
- Criar: src/Foundation/Vinx.Erp.Foundation/Identity/SelfSessionRevocationService.cs
- Criar: src/Foundation/Vinx.Erp.Foundation/Identity/SelfSessionRevocationResult.cs
- Criar: migration EF AddSessionGateAndSelfRevocation e designer correspondente
- Modificar: src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations/FoundationDbContextModelSnapshot.cs
- Modificar: docs/database/foundation-schema.snapshot.sql
- Modificar: tests/public/Vinx.Erp.Foundation.IntegrationTests/SessionMigrationTests.cs
- Criar: tests/public/Vinx.Erp.Foundation.IntegrationTests/SelfSessionRevocationServiceTests.cs

**Interfaces:**
- Consome ActorContext, FoundationDbContext, AuditAppender, AuditSource e CancellationToken.
- Produz Task<SelfSessionRevocationResult> RevokeAllAsync(ActorContext actor, AuditSource source, CancellationToken cancellationToken), com estados Revoked, NoChange e Unavailable.

~~~csharp
public enum SelfSessionRevocationState { Revoked, NoChange, Unavailable }

public sealed record SelfSessionRevocationResult(SelfSessionRevocationState State)
{
    public static SelfSessionRevocationResult Revoked() => new(SelfSessionRevocationState.Revoked);
    public static SelfSessionRevocationResult NoChange() => new(SelfSessionRevocationState.NoChange);
    public static SelfSessionRevocationResult Unavailable() => new(SelfSessionRevocationState.Unavailable);
}
~~~

- [ ] **Passo 1: Escrever testes RED de migration e serviço.**

Cobrir função foundation.revoke_all_user_sessions(uuid): bloqueia a linha de users, usa um único clock_timestamp(), avança a barreira somente se existir sessão ativa, revoga todas as linhas ativas do próprio usuário com motivo fixo self_revoke_all e retorna a contagem. Uma NoChange não altera nem reduz a barreira. Testar grants: runtime só executa a função; owner de identidade recebe as colunas necessárias; runtime continua sem UPDATE direto. Testar sucesso com duas sessões, no-op posterior, falha de audit.append_event com rollback e duas chamadas concorrentes com uma única auditoria de sucesso; depois da NoChange, uma prova antiga com session_id ainda não observado deve ser negada pela barreira.

- [ ] **Passo 2: Rodar RED.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~SelfSessionRevocationServiceTests|FullyQualifiedName~SessionMigrationTests'
~~~

Esperado: falhas por serviço/função inexistentes, nunca por fixture ou Docker.

- [ ] **Passo 3: Implementar mínimo.**

Criar função SECURITY DEFINER com search_path TO foundation, pg_catalog, revogar PUBLIC, definir owner vinx_identity_owner e conceder somente EXECUTE ao runtime. Serviço abre transação, chama a função parametrizada, insere identity_sessions_revoked apenas quando mudou estado, usa metadata {"revoked_session_count":inteiro}, confirma ambas operações juntas e retorna Unavailable sem vazar exceção. Não conceder update direto ao runtime. A migration atualiza docs/database/foundation-schema.snapshot.sql; seu Down falha fechado se houver barreira, revogação ou evento identity_sessions_revoked persistido, para nunca desfazer uma transição observável, e só remove função/grants quando não houver estado.

- [ ] **Passo 4: Rodar GREEN e conferir migration.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~SelfSessionRevocationServiceTests|FullyQualifiedName~SessionMigrationTests|FullyQualifiedName~InternalSessionAdmissionServiceTests'
./scripts/verify-foundation-database.sh
~~~

- [ ] **Passo 5: Commit.**

~~~bash
git add src/Foundation/Vinx.Erp.Foundation/Identity src/Foundation/Vinx.Erp.Foundation/Persistence/Migrations tests/public/Vinx.Erp.Foundation.IntegrationTests docs/database/foundation-schema.snapshot.sql
git commit -m "feat(identity): revoke own sessions atomically"
~~~

### Tarefa 2: Aplicar gate interno a endpoints protegidos

**Arquivos:**
- Criar: src/Foundation/Vinx.Erp.Foundation/Identity/InternalSessionGateMiddleware.cs
- Criar: src/Foundation/Vinx.Erp.Foundation/Identity/ActorContextHttpContextExtensions.cs
- Modificar: src/Foundation/Vinx.Erp.Foundation/Identity/InternalIdentityResolver.cs
- Modificar: src/Hosts/Vinx.Erp.ApiHost/Program.cs
- Modificar: tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs
- Criar ou modificar: tests/public/Vinx.Erp.Foundation.IntegrationTests/IdentityGateEndpointTests.cs
- Modificar: tests/public/Vinx.Erp.Foundation.IntegrationTests/AuthorizationEndpointTests.cs

**Interfaces:**
- Consome IIdentityProvider, InternalIdentityResolver, InternalSessionAdmissionService, ServerCorrelationContext e metadata de autorização.
- Produz HttpContext.GetRequiredActorContext() para handlers protegidos.

~~~csharp
private static readonly object ActorContextKey = new();

public static void SetActorContext(this HttpContext context, ActorContext actor) =>
    context.Items[ActorContextKey] = actor;

public static ActorContext GetRequiredActorContext(this HttpContext context) =>
    context.Items.TryGetValue(ActorContextKey, out var value) && value is ActorContext actor
        ? actor
        : throw new InvalidOperationException("The identity gate did not establish an actor.");
~~~

- [ ] **Passo 1: Escrever testes RED do pipeline.**

Provar que endpoint protegido com JWT válido chega ao handler apenas após admissão e recebe ator interno. Provar que identidade desconhecida, prova inválida, sessão revogada e barreira igual/anterior retornam 401 antes do handler; falha na resolução ou admissão retorna 503 com identity_gate_unavailable; health e endpoint AllowAnonymous não consultam sessão. Provar que o provider é consultado uma única vez por requisição protegida. Atualizar expectativas de auditoria existentes para incluir somente admissão legítima nova, sem duplicá-la em repetição ativa.

- [ ] **Passo 2: Rodar RED.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~IdentityGateEndpointTests|FullyQualifiedName~AuthorizationEndpointTests'
~~~

Esperado: handler atual ainda resolve identidade sozinho e não há gate.

- [ ] **Passo 3: Implementar mínimo.**

Adicionar UseRouting, registrar InternalSessionAdmissionService e inserir middleware entre autenticação e autorização. Middleware ignora endpoint anônimo ou sem autorização; para endpoint protegido extrai ExternalIdentityProof uma única vez, passa proof.Key a InternalIdentityResolver.ResolveAsync(ExternalIdentityKey, ...) e passa a mesma prova a AdmitAsync com AuditSource.Web antes de gravar ator sob chave privada tipada. InternalIdentityResolver não consulta IIdentityProvider. Denied não expõe razão; falha de resolução ou admissão escreve somente código seguro. Remover resolução direta de identidade do handler de capabilities.

- [ ] **Passo 4: Rodar GREEN.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~IdentityGateEndpointTests|FullyQualifiedName~AuthorizationEndpointTests|FullyQualifiedName~InternalSessionAdmissionServiceTests'
~~~

- [ ] **Passo 5: Commit.**

~~~bash
git add src/Foundation/Vinx.Erp.Foundation/Identity src/Hosts/Vinx.Erp.ApiHost/Program.cs tests/public/Vinx.Erp.Foundation.IntegrationTests
git commit -m "feat(identity): gate protected requests by session"
~~~

### Tarefa 3: Expor revoke-all e registrar evidência pública

**Arquivos:**
- Modificar: src/Hosts/Vinx.Erp.ApiHost/Program.cs
- Modificar: tests/public/Vinx.Erp.Foundation.IntegrationTests/IdentityGateEndpointTests.cs
- Criar: docs/verification/21-session-gate-revocation-test-cases.md
- Modificar: docs/modules/foundation.md
- Modificar: docs/superpowers/specs/2026-08-09-identity-sessions-deactivation-design.md

**Interfaces:**
- Consome HttpContext.GetRequiredActorContext() e SelfSessionRevocationService.RevokeAllAsync.
- Produz POST /foundation/identity/sessions/revoke-all, sem request body, com 204, 401 pelo gate ou 503 identity_gate_unavailable.

~~~csharp
app.MapPost("/foundation/identity/sessions/revoke-all", async (
    HttpContext context,
    SelfSessionRevocationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.RevokeAllAsync(
        context.GetRequiredActorContext(), AuditSource.Web, cancellationToken);
    return result.State is SelfSessionRevocationState.Revoked or SelfSessionRevocationState.NoChange
        ? Results.NoContent()
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?> { ["code"] = "identity_gate_unavailable" });
}).RequireAuthorization();
~~~

- [ ] **Passo 1: Escrever teste RED do endpoint.**

Criar duas sessões próprias por prova válida, chamar rota sem corpo e verificar 204, barreira avançada, duas sessões revogadas, uma auditoria global com contagem e 401 na próxima chamada com prova antiga. Cobrir chamada não autenticada (401) e repetição concorrente (204, uma transição, uma auditoria). Nenhuma resposta contém ID externo ou detalhe de sessão.

- [ ] **Passo 2: Rodar RED.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~IdentityGateEndpointTests
~~~

Esperado: rota inexistente ou ainda sem delegar ao serviço.

- [ ] **Passo 3: Implementar mínimo e manifesto.**

Mapear rota com RequireAuthorization(), obter somente ator do gate e converter Revoked/NoChange em NoContent; Unavailable mantém código seguro identity_gate_unavailable. Criar matriz BDD em docs/verification/21-session-gate-revocation-test-cases.md, atualizar responsabilidade do Foundation e manter #79, #80, #81 e #18 explicitamente adiadas.

- [ ] **Passo 4: Rodar GREEN.**

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~IdentityGateEndpointTests|FullyQualifiedName~SelfSessionRevocationServiceTests|FullyQualifiedName~AuthorizationEndpointTests'
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
~~~

- [ ] **Passo 5: Commit.**

~~~bash
git add src/Hosts/Vinx.Erp.ApiHost/Program.cs tests/public/Vinx.Erp.Foundation.IntegrationTests docs/verification/21-session-gate-revocation-test-cases.md docs/modules/foundation.md docs/superpowers/specs/2026-08-09-identity-sessions-deactivation-design.md
git commit -m "feat(identity): expose self revoke-all"
~~~

## Auto-revisão do plano

- Cobertura: cada requisito da #21 aparece em tarefa e BDD; #79, #80, #81 e #18 permanecem fora.
- Consistência: gate usa prova e admissão existentes; somente função SQL muda barreira e sessões; endpoint não aceita alvo do cliente.
- Segurança: negações sem pessoa interna não criam ator sintético; mutação e auditoria usam uma transação; nenhum dado sensível entra em logs ou documentos.
- Tamanho: três tarefas e arquivos reutilizados mantêm a PR abaixo de 25 arquivos e 800 linhas manuais; reestimar antes do push.
