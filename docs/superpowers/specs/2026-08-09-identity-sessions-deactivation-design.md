# Sessões revogáveis e desativação efetiva

## Status

Decisão aprovada em 09/08/2026. Esta decisão detalha a fatia de sessões da
issue #21, subordinada à issue #44. MFA e AAL permanecem na issue #18.

A admissão interna da sessão já foi entregue pela PR #105: estado EF, lock
atômico, auditoria global e logs operacionais. A fatia atual da #21 registra o
gate HTTP e expõe somente a revogação global da própria pessoa; desativação e
demais operações de sessão continuam posteriores.

## Objetivo

Fazer o VINX negar, na próxima chamada autenticada, uma sessão revogada ou um
usuário desativado, sem trocar o Supabase Auth, guardar credenciais do
provider ou criar acesso por e-mail.

## Decisão

O JWT Supabase continua provando somente identidade externa válida. Um gate
interno, depois de validar o JWT, resolve `issuer` + `subject`, verifica a
sessão EF e cria o ator interno. A sessão EF é autoridade para acesso ao
VINX, não uma credencial e nem espelho da tabela `auth.sessions`.

O pipeline obrigatório é `UseAuthentication`, `UseIdentityGate` e
`UseAuthorization`. O gate encerra com `401` ou `503` antes de qualquer
handler. Nenhum endpoint VINX protegido resolve identidade por conta própria;
ele lê o `ActorContext` tipado que o gate guardou no `HttpContext`. Endpoint
novo somente com `RequireAuthorization()` também passa pelo gate. Health e
rotas declaradas `AllowAnonymous` não usam o ator interno.

O adapter Supabase exige `session_id` UUID e uma lista `amr` válida. A data de
autenticação da sessão é o menor timestamp das entradas `amr` cujo método não
é `token_refresh`. Isso é um contrato explícito da configuração Supabase
inicial, validado com token real do ambiente local antes de aceitar a PR. Sem
esses dados, a chamada recebe `401` e não altera o banco.

Após revogação global, uma sessão só pode ser admitida se essa data de
autenticação for posterior a `sessions_valid_after`. Logo, refresh de sessão
antiga não contorna a barreira. Se o token real não preservar essa evidência,
esta parte não avança: a alternativa exigirá aprovação para integrar logout ou
banimento do provider com outbox e retry.

Supabase documenta `session_id` como identificador único da sessão no JWT.
`amr` registra métodos e timestamps de autenticação; `iat` não serve para a
barreira, pois é renovado a cada access token.

## Fora de escopo

- Logout, banimento, consulta a `auth.sessions` ou uso administrativo do
  Supabase.
- Persistência de JWT ou refresh token, e-mail, IP, User-Agent, limite de
  dispositivos, timeout por inatividade e remoção física de sessão.
- MFA, AAL, recuperação, rate limit, reativação e administração por MCP.
- Segregação de função e regras de aprovação de módulos.

## Modelo persistido

`User` recebe:

- `SessionsValidAfter`: barreira UTC para sessões novas.
- `DeactivatedAt`, `DeactivatedByUserId` e `DeactivationReason`: estado global
  de acesso, com motivo obrigatório na desativação.
- `RowVersion`: concorrência otimista para as duas transições.

`UserSession` é uma entidade global em `foundation.user_sessions`:

- `Id`, `UserId`, `Issuer`, `ProviderSessionId` UUID;
- `ProviderAuthenticatedAt`, `FirstSeenAt`, `TokenExpiresAt`;
- `RevokedAt`, `RevokedByUserId`, `RevocationReason` e `RowVersion`.

Há unicidade em `(issuer, provider_session_id)`, índice por
`(user_id, revoked_at)` e FKs restritivas. A sessão é criada uma vez; sessão
revogada nunca é reativada. Runtime recebe somente `SELECT`, `INSERT` e
`EXECUTE` para funções estreitas; não recebe `UPDATE` ou `DELETE` direto.

## Fluxo de acesso

1. `JwtBearer` valida assinatura, issuer, audience e validade do token.
2. `IIdentityProvider` extrai identidade externa, `session_id`, `amr`, `iat`
   e `exp` apenas de claims já validadas.
3. O gate resolve o `User` por `issuer` + `subject`, sem e-mail, e bloqueia
   usuário desativado.
4. O gate busca a sessão por `issuer` + `provider_session_id`. Se ausente,
   admite-a atomicamente somente quando a barreira de usuário permitir.
5. Sessão revogada, anterior à barreira ou vinculada a outro usuário recebe
   `401`. Falha de banco ou auditoria recebe `503 identity_gate_unavailable`.
6. O gate guarda `ActorContext` no `HttpContext`; endpoints protegidos não
   resolvem identidade novamente nem aceitam correlação do cliente.

`session_id` inválido é qualquer valor que não converte para UUID.
`amr` inválido é claim ausente, não-array, vazio, item sem método não vazio,
timestamp que não seja inteiro Unix representável, ausência de item diferente
de `token_refresh` ou timestamp autenticador posterior a `iat`. O timestamp
efetivo é o menor timestamp não-refresh. O adapter não registra claims nem
tokens.

`SessionsValidAfter` começa nulo. Revogação global usa `clock_timestamp()` do
banco, avança o valor monotonicamente e admite somente timestamp efetivo
estritamente maior. Igualdade e instante anterior são negados; autenticação no
mesmo segundo exige novo login no segundo seguinte. Teste local obrigatório
compara dois JWTs reais da mesma sessão, antes e depois de refresh: mesmo
`session_id`, `iat` maior e timestamp efetivo inalterado. Tokens não entram em
logs, fixtures ou Markdown.

## Operações e auditoria

Esta fatia expõe somente `POST /foundation/identity/sessions/revoke-all`, sem
corpo e sem identificador de usuário ou empresa. Ela revoga sessões próprias,
inclusive a corrente, e responde `204`. Listagem e revogação individual ficam
na #79; portanto sessão de outra pessoa não é uma superfície desta PR.

Sessões são globais; por isso `AuditEvent` e `AuditEventData` ganham
`AuditScope` com valores `global` e `company`. A migration substitui
`audit.append_event` para receber esse escopo e cria a constraint:
`global` exige `requested_company_id IS NULL`; `company` exige empresa não
nula. Não há empresa fictícia, `Guid.Empty` ou exceção por nome de ação.

Os eventos globais estáveis são `identity_session_admitted`,
`identity_session_admission_denied`, `identity_session_revoked`, `identity_sessions_revoked`,
`identity_session_revoke_denied` e `identity_user_deactivated`. Recurso é
`user_session` ou `user`; metadata contém somente contagens e não inclui
`provider_session_id`. Sucesso é inserido na mesma transação da mutação;
falha da função reverte a mutação e retorna `503 identity_gate_unavailable`.
Índices, snapshot, privilégios da função e testes append-only são atualizados
na mesma migration.

Na admissão, barreira temporal, sessão revogada e colisão de identidade usam
`identity_session_admission_denied`, resultado `denied`, razão estável e
metadata `{}`. A colisão entre usuários não informa recurso nem identidade do
outro usuário. Falha ao gravar auditoria reverte a nova sessão e resulta em
indisponibilidade; falha técnica não cria uma segunda auditoria.

## Desativação

A segunda PR adiciona comando global de desativação. Como RLS por empresa não
permite provar vínculos do alvo em outros grupos, o comando usa uma função SQL
estreita `SECURITY DEFINER`, sem conceder leitura global ao runtime. EF mantém
o modelo e chama somente esse comando crítico, prática já permitida para RLS.

A função recebe empresa âncora, ator, alvo, motivo e correlação. Na mesma
transação, ela valida membership ativo do ator na âncora, capability
`identity.user.deactivate` com escopo exatamente `group`, deriva o grupo,
bloqueia a linha do alvo, verifica todos os vínculos ativos do alvo sem RLS,
bloqueia auto-desativação e recusa vínculo fora do grupo. Então desativa o
usuário, avança a barreira, revoga sessões conhecidas e grava a auditoria
global. Falha ao auditar faz rollback e retorna `503 identity_gate_unavailable`.

O mesmo lock do usuário alvo é contrato obrigatório para criação e restauração
futuras de membership. Não existe fluxo runtime dessas mutações nesta fatia;
quando existir, ele deve adquirir o lock antes de validar ou gravar vínculo.

Escopo `company` não concede poder para desativar usuário globalmente. Uma
autoridade de plataforma não será introduzida nesta fatia.

## Entregas e ordem

1. PR-base de auditoria global: escopo tipado, função, grants, snapshot e
   rollback. Não introduz sessão, endpoint ou alteração de provider.
2. Microfatia de prova Supabase: `session_id` UUID e `amr` com autenticação
   não-refresh, `401` fail-closed e sem persistência; não introduz sessão EF,
   gate ou revogação.
3. PR de admissão de sessão, já mesclada como #105: modelo EF, lock,
   admissão, auditoria e logs.
4. PR de gate e `revoke-all` próprio da #21: middleware, função SQL estreita,
   revogação global atômica e endpoint protegido. A rota é
   `POST /foundation/identity/sessions/revoke-all`, sem corpo e sem alvo; usa
   somente `ActorContext`, responde `204` para `Revoked`/`NoChange` e
   `503 application/json` com objeto de uma única propriedade `code` e valor
   `identity_gate_unavailable` para indisponibilidade. A idempotência preserva
   o mesmo estado final: a concorrência pode responder `204/204` quando ambas
   as requisições passam pelo gate antes da barreira, ou `204/401` quando uma
   observa a barreira no gate. O teste determinístico após o primeiro `204`
   espera `401`.
5. PR de desativação: capability de grupo, comando transacional, migration,
   documentação e testes públicos, sobre as três PRs anteriores.
6. Depois de todas mergeadas, a issue #18 implementa MFA step-up/AAL sobre a
   evidência de sessão já admitida.

## Critérios verificáveis

- duas chamadas simultâneas da mesma sessão geram uma única linha;
- refresh com mesmo `session_id` não reativa sessão revogada;
- sessão anterior à barreira, inclusive ainda não observada pelo VINX, recebe
  `401`; autenticação posterior é admitida;
- claim ausente ou malformada falha fechada e não grava dados;
- rota protegida que só exige autorização não alcança o handler com identidade
  desconhecida, sessão revogada ou usuário desativado;
- auditoria global exige escopo coerente com empresa nula e preserva
  append-only;
- desativação bloqueia qualquer empresa na próxima chamada e registra ator,
  alvo, motivo e correlação;
- comando de desativação nega grant `company`, vínculo do alvo fora do grupo
  e corrida com criação ou restauração de vínculo;
- indisponibilidade de auditoria não desativa nem revoga parcialmente;
- todos os testes ficam em `tests/public`; `quality/private` não é lido nem
  executado.

## Adiamentos explícitos

Esta entrega não inclui listagem ou revogação individual (#79), interface de
sessões (#80), administração de sessões (#81), MFA/AAL (#18), recuperação
(#19) ou logout/banimento no provedor (#20). Esta fatia inclui migration de
sessão/revogação e gate de identidade. Não há mudança adicional de modelo EF
ou `ModelSnapshot`, nem entrada nos adiamentos #79, #80, #81, #18, #19 ou #20.
