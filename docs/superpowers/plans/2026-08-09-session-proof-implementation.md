# Prova mínima de sessão Supabase — plano de implementação

> **Para agentes:** subskill obrigatório: `subagent-driven-development`. Cada tarefa usa TDD e revisão independente.

**Objetivo:** exigir prova mínima de sessão Supabase antes da resolução da identidade interna, sem introduzir estado de sessão, EF ou revogação.

**Arquitetura:** `JwtBearer` continua validando assinatura, issuer, audience e lifetime. `SupabaseIdentityProvider` converte somente claims já autenticadas em `ExternalIdentityProof`; `InternalIdentityResolver` consulta EF exclusivamente por `proof.Key`. Uma prova ausente ou inválida retorna `401` pelo endpoint existente.

**Tecnologias:** .NET 10, ASP.NET Core Minimal API, EF Core/Npgsql, xUnit, `WebApplicationFactory`, JWT/JWKS RSA controlado.

## Restrições globais

- Base: `origin/main` no commit `6b8eb84`; branch `codex/issue-21-session-proof`.
- Provider inicial permanece Supabase Auth; EF continua persistência canônica, mas esta PR não altera EF/schema/migration/grant/RLS.
- Não criar `UseIdentityGate`, endpoint, `UserSession`, `revoke-all`, MFA/AAL, recuperação, dispositivo, desativação, provider alternativo, harness/CI ou dependência nova.
- Não armazenar nem registrar JWT, refresh token, e-mail, IP, User-Agent, AMR bruto ou conteúdo de claims.
- Todos os testes ficam em `tests/public`; nunca ler, listar ou executar `quality/private`.
- Validar `session_id`, `iat`, `exp` e `amr` de modo fail-closed. `iat` nunca é evidência de autenticação; somente ancora timestamps AMR, e `exp` deve ser estritamente posterior a `iat`.
- Depois de `JwtBearer` .NET 10, um array JWT `amr` vira uma claim JSON por item. `amr` exige uma ou mais claims `JsonClaimValueTypes.Json`, cada uma contendo objeto JSON; não exigir uma única claim que contenha o array inteiro. Um objeto AMR top-level e array de um objeto são indistinguíveis nesta porta e não recebem regras divergentes.
- Antes do push: testes focados durante desenvolvimento; um único `./scripts/verify-public.sh`; revisão Qwen 3.8 Max; um único draft PR.

---

## Estrutura de arquivos

| Arquivo | Responsabilidade |
|---|---|
| `src/Foundation/Vinx.Erp.Foundation/Identity/IIdentityProvider.cs` | contratos imutáveis de chave e prova externa |
| `src/Foundation/Vinx.Erp.Foundation/Identity/SupabaseIdentityProvider.cs` | extração estrita de claims Supabase |
| `src/Foundation/Vinx.Erp.Foundation/Identity/InternalIdentityResolver.cs` | lookup EF por `proof.Key` |
| `tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseIdentityProviderTests.cs` | contrato puro do parser |
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs` | claims fake válidas e JWT/JWKS com AMR JSON real |
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/AuthorizationEndpointTests.cs` | fronteira HTTP 401 e não regressão |
| `docs/modules/foundation.md` | contrato durável de prova mínima |
| `docs/superpowers/specs/2026-08-09-identity-sessions-deactivation-design.md` | ordem real: prova primeiro, EF/gate depois |

Estimativa: 8 arquivos, 341–511 linhas manuais. Um subsistema: ponte de identidade. A estimativa não alcança os limites de decomposição.

### Task 1: Fixar a fronteira HTTP antes do parser

**Arquivos:**

- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs`
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/AuthorizationEndpointTests.cs`

**Consome:** JWT validado por `JwtBearer`, metadata e JWKS RSA existentes.

**Produz:** fixtures que representam uma prova Supabase válida ou uma falha de prova, sem alterar produção.

- [ ] **Passo 1: Caracterizar forma real do AMR após `JwtBearer`.**

  Com JWT assinado/JWKS controlado cujo payload contém array `amr` de dois objetos, capturar o principal depois da autenticação e provar: duas claims `amr`, cada uma com `ValueType == JsonClaimValueTypes.Json` e valor JSON de objeto. O handler fake usará exatamente essa forma pós-validação, nunca uma string com array inteiro. Registrar no teste que a porta não distingue payload AMR objeto top-level de array com único objeto porque o handler perde esse contêiner.

- [ ] **Passo 2: Escrever testes HTTP RED para ausência e malformação da prova.**

  Adicionar `JwtTokenVariation` para `MissingSessionId`, `MalformedSessionId`, `MissingAmr`, `EmptyAmr`, `RefreshOnlyAmr`, `AuthenticatorAfterIssuedAt` e `IssuedAtAtOrAfterExpiresAt`. Cada token mantém assinatura, issuer, audience e lifetime válidos. A última variação usa `expires` futuro e `iat >= exp` explícito, para o bearer aceitar lifetime e o parser negar a relação da prova. A teoria exige `401` e body vazio. Não criar caso `AmrNotArray`: um objeto top-level é indistinguível de array com um objeto após `JsonWebTokenHandler`.

  ```csharp
  [Theory]
  [InlineData(JwtTokenVariation.MissingSessionId)]
  [InlineData(JwtTokenVariation.RefreshOnlyAmr)]
  public async Task Capability_map_rejects_a_valid_jwt_without_session_proof(
      JwtTokenVariation variation)
  {
      // Bearer controlado; Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
  }
  ```

- [ ] **Passo 3: Fazer factory emitir claims reais de forma.**

  Token válido contém `session_id` UUID no formato `D`, `iat` e `exp` Unix seconds com `exp = iat + 1` segundo, e `amr` como array JSON de objetos `{ "method", "timestamp" }`. Incluir dois métodos autenticadores fora de ordem e um `token_refresh` posterior para provar que a prova futura usará menor timestamp não-refresh. O handler fake recebe uma claim JSON por item AMR, igual à forma caracterizada; testes antigos continuam focados em membership/capability. Construir `exp` somente pelo parâmetro `expires` do JWT e `iat` somente como claim numérica, sem duplicar `exp` manualmente.

- [ ] **Passo 4: Provar não-regressão do bearer no RED.**

  Adicionar factory de teste com `IIdentityProvider` contador. Para JWT de assinatura, issuer, audience ou expiração inválidos, exigir `401` e contador zero: o bearer recusa antes de resolver/provar identidade. Para JWT válido com prova, manter `400 company_context_required`, provando que chegou ao handler.

- [ ] **Passo 5: Rodar RED.**

  Executar:

  ```bash
  DOCKER_CONTEXT=desktop-linux dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~AuthorizationEndpointTests"
  ```

  Esperado: novas variações retornam `400 company_context_required`, não `401`, porque provider atual ainda aceita só `issuer` e `subject`. Casos de assinatura/issuer/audience/expiração inválidos permanecem `401` pelo bearer.

### Task 2: Produzir prova tipada e parser fail-closed

**Arquivos:**

- Modificar: `src/Foundation/Vinx.Erp.Foundation/Identity/IIdentityProvider.cs`
- Modificar: `src/Foundation/Vinx.Erp.Foundation/Identity/SupabaseIdentityProvider.cs`
- Modificar: `src/Foundation/Vinx.Erp.Foundation/Identity/InternalIdentityResolver.cs`
- Renomear/modificar: `tests/public/Vinx.Erp.Foundation.UnitTests/InternalIdentityResolverTests.cs` para `tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseIdentityProviderTests.cs`
- Modificar: `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/FoundationApiFactory.cs` somente para adequar `CountingIdentityProvider` ao método da porta alterada.

**Consome:** fixtures RED da Task 1.

**Produz:**

```csharp
public sealed record ExternalIdentityProof(
    ExternalIdentityKey Key,
    Guid ProviderSessionId,
    DateTimeOffset ProviderAuthenticatedAt,
    DateTimeOffset TokenExpiresAt);

public interface IIdentityProvider
{
    bool TryGetExternalIdentityProof(
        ClaimsPrincipal principal,
        out ExternalIdentityProof proof);
}
```

- [ ] **Passo 1: Escrever testes unitários RED do contrato.**

  Casos válidos usam issuer configurado, subject não vazio, UUID `D` não vazio, `exp = iat + 1`, uma ou mais claims AMR JSON de objeto e menor timestamp não-refresh. Casos inválidos: claim escalar obrigatória ausente/duplicada/vazia; issuer diferente; UUID `N`, braces ou vazio; Unix não-inteiro/fora de intervalo; `exp == iat`; `exp < iat`; AMR escalar, JSON inválido, item não-objeto, `method` ausente/vazio/duplicado, `timestamp` ausente/fracionário/duplicado/fora de intervalo, apenas refresh e autenticador posterior a `iat`. Não testar rejeição de objeto top-level AMR porque ele é indistinguível do array válido com um item nesta porta.

  ```csharp
  [Fact]
  public void Try_get_external_identity_proof_uses_earliest_non_refresh_amr_timestamp()
  {
      // prova válida tem refresh mais novo; Assert.Equal(expectedUnix, proof.ProviderAuthenticatedAt)
  }
  ```

  Provar também que e-mail e correlação são ignorados e que método desconhecido não vazio conta como autenticação real.

- [ ] **Passo 2: Rodar RED do contrato.**

  Executar:

  ```bash
  dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter "FullyQualifiedName~SupabaseIdentityProviderTests"
  ```

  Esperado: falha de compilação por ausência de `ExternalIdentityProof` e `TryGetExternalIdentityProof`.

- [ ] **Passo 3: Implementar mínimo.**

  Manter `ExternalIdentityKey`; acrescentar `ExternalIdentityProof`; substituir método da porta. Em `SupabaseIdentityProvider`, exigir exatamente uma claim escalar para `iss`, `sub`, `session_id`, `iat` e `exp`; exigir uma ou mais claims `amr` com `ValueType == JsonClaimValueTypes.Json`. Cada claim AMR deve ser objeto JSON, com exatamente uma propriedade `method` string não vazia e exatamente uma `timestamp` `Int64`; campos extras são ignorados. Usar comparações ordinais, `Guid.TryParseExact(value, "D", out value)`, parse `Int64` invariável e `JsonDocument` local. Rejeitar `exp <= iat`, forma AMR mista ou escalar, e qualquer ambiguidade observável. Duplicidade da propriedade JSON `amr`, assim como objeto top-level versus array de um item, não é observável após o handler; não prometer sua detecção nesta porta. Retornar `false` em todo input adversarial e nunca registrar claims. Em `InternalIdentityResolver`, trocar somente `key` por `proof.Key`, preservando `AsNoTracking`, `SingleOrDefaultAsync` e `CancellationToken`. Em `CountingIdentityProvider`, implementar o novo método e incrementar o contador antes de devolver prova válida; nenhuma outra fixture ou teste de integração muda nesta tarefa.

- [ ] **Passo 4: Rodar GREEN focado.**

  Executar:

  ```bash
  dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --no-restore --filter "FullyQualifiedName~SupabaseIdentityProviderTests"
  DOCKER_CONTEXT=desktop-linux dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~AuthorizationEndpointTests"
  dotnet build Vinx.Erp.sln --no-restore
  ```

  Esperado: prova válida chega ao handler e retorna `400` sem `companyId`; prova inválida retorna `401`; identidade desconhecida continua sem JIT provisioning; JWT criptograficamente inválido continua negado pelo bearer.

### Task 3: Fixar decisão durável e fechar verificações

**Arquivos:**

- Modificar: `docs/modules/foundation.md`
- Modificar: `docs/superpowers/specs/2026-08-09-identity-sessions-deactivation-design.md`

**Consome:** contrato e comportamento verdes.

**Produz:** documentação portuguesa que distingue prova de sessão de gate revogável.

- [ ] **Passo 1: Atualizar Foundation.**

  Registrar que `session_id` UUID e AMR com autenticação não-refresh são exigidos antes da resolução; ausência/malformação retorna `401`; nenhum token ou dado pessoal é persistido. Não prometer revogação antes da PR EF/gate.

- [ ] **Passo 2: Atualizar desenho.**

  Em “Entregas e ordem”, inserir esta microfatia entre auditoria global e sessão EF/gate. O restante do desenho permanece inalterado.

- [ ] **Passo 3: Revisar escopo.**

  Executar:

  ```bash
  git diff --check
  git diff --name-only origin/main...HEAD
  git diff --check origin/main...HEAD
  ```

  Esperado: diff sem whitespace; lista de arquivos coincide com a tabela deste plano; nenhuma produção fora da ponte de identidade ou teste público foi adicionada.

- [ ] **Passo 4: Verificar Supabase local sem segredos.**

  Antes de aceitar a PR, observar dois JWTs reais da mesma sessão — login e refresh — e registrar apenas: `session_id` presente/igual, `iat` maior após refresh, AMR presente/array e menor timestamp não-refresh inalterado. Não gravar token, timestamp, ID ou payload. Se qualquer condição falhar, parar: não substituir prova por `iat`.

- [ ] **Passo 5: Fechar PR.**

  Após revisão independente e Qwen 3.8 Max, executar uma única vez:

  ```bash
  ./scripts/verify-public.sh
  ```

  Se verde, criar commit convencional, push e um único draft PR para #21. Se a verificação local real de Supabase estiver indisponível, não declarar aceite completo nem criar PR como pronta para merge.

## Auto-revisão do plano

- Cobertura: forma AMR pós-bearer, contrato, parser, fronteira HTTP, `exp > iat`, regressão JWT, documentação, teste real provider e gate público têm tarefa.
- Consistência: todo consumidor usa `ExternalIdentityProof.Key`; `iat` não aparece no tipo público.
- Escopo: nenhum passo cria persistência, middleware/gate, operação de revogação ou trabalho adiado.
- Sem placeholders: paths, contrato, casos e comandos estão explícitos.
