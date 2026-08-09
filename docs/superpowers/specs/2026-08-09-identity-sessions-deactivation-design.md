# Sessões revogáveis e desativação efetiva

## Status

Proposta para revisão antes do plano de implementação. Esta decisão detalha a
fatia de sessões da issue #21, subordinada à issue #44. MFA e AAL permanecem
na issue #18.

## Objetivo

Fazer o VINX negar, na próxima chamada autenticada, uma sessão revogada ou um
usuário desativado, sem trocar o Supabase Auth, guardar credenciais do
provider ou criar acesso por e-mail.

## Decisão

O JWT Supabase continua provando somente identidade externa válida. Um gate
interno, depois de validar o JWT, resolve `issuer` + `subject`, verifica a
sessão EF e cria o ator interno. A sessão EF é autoridade para acesso ao
VINX, não uma credencial e nem espelho da tabela `auth.sessions`.

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

- `Id`, `UserId`, `Issuer`, `ProviderSessionId`;
- `ProviderAuthenticatedAt`, `FirstSeenAt`, `TokenExpiresAt`;
- `RevokedAt`, `RevokedByUserId`, `RevocationReason` e `RowVersion`.

Há unicidade em `(issuer, provider_session_id)`, índice por
`(user_id, revoked_at)` e FKs restritivas. A sessão é criada uma vez; sessão
revogada nunca é reativada. Runtime recebe somente privilégios necessários de
`SELECT`, `INSERT` e `UPDATE`, sem `DELETE`.

## Fluxo de acesso

1. `JwtBearer` valida assinatura, issuer, audience e validade do token.
2. `IIdentityProvider` extrai identidade externa, `session_id`, `amr` e `exp`
   apenas de claims já validadas.
3. O gate resolve o `User` por `issuer` + `subject`, sem e-mail, e bloqueia
   usuário desativado.
4. O gate busca a sessão por `issuer` + `provider_session_id`. Se ausente,
   admite-a atomicamente somente quando a barreira de usuário permitir.
5. Sessão revogada, anterior à barreira ou vinculada a outro usuário recebe
   `401`. Falha de banco ou auditoria recebe `503 identity_gate_unavailable`.
6. O gate guarda `ActorContext` no `HttpContext`; endpoints protegidos não
   resolvem identidade novamente nem aceitam correlação do cliente.

## Operações e auditoria

A primeira PR expõe somente operações do próprio usuário:

- listar sessões ativas, com dados redigidos e identificador interno;
- revogar uma sessão própria, idempotentemente;
- revogar todas as sessões próprias, inclusive a corrente.

Sessão de outro usuário parece inexistente (`404`) e a tentativa negada é
auditada. Sessões são globais; por isso auditoria passa a representar escopo
`global` ou `company`, sem usar empresa fictícia ou `Guid.Empty`.

## Desativação

A segunda PR adiciona comando global de desativação. Ele exige capability
`identity.user.deactivate` com escopo `group`, usa uma empresa do grupo como
âncora de autorização, bloqueia auto-desativação e recusa alvo com vínculo
ativo fora do grupo do ator. Desativação, avanço da barreira, revogação das
sessões conhecidas e evento de auditoria são uma única transação. Falha ao
auditar faz rollback e retorna `503 identity_gate_unavailable`.

Escopo `company` não concede poder para desativar usuário globalmente. Uma
autoridade de plataforma não será introduzida nesta fatia.

## Entregas e ordem

1. PR de sessão: adapter de claims, modelo EF, gate, operações próprias,
   auditoria global mínima, migration, documentação e testes públicos.
2. PR de desativação: capability de grupo, comando transacional, migration,
   documentação e testes públicos.
3. Depois de ambas mergeadas, a issue #18 implementa MFA step-up/AAL sobre a
   evidência de sessão já admitida.

## Critérios verificáveis

- duas chamadas simultâneas da mesma sessão geram uma única linha;
- refresh com mesmo `session_id` não reativa sessão revogada;
- sessão anterior à barreira, inclusive ainda não observada pelo VINX, recebe
  `401`; autenticação posterior é admitida;
- claim ausente ou malformada falha fechada e não grava dados;
- desativação bloqueia qualquer empresa na próxima chamada e registra ator,
  alvo, motivo e correlação;
- indisponibilidade de auditoria não desativa nem revoga parcialmente;
- todos os testes ficam em `tests/public`; `quality/private` não é lido nem
  executado.
