# Sessão BFF do navegador — plano curto #219

**Objetivo:** permitir login e sessão autenticada same-origin para o frontend,
sem dar ao navegador token, refresh token, SDK ou acesso direto ao Supabase.

**Limite:** previsão de 12–16 arquivos não-testes e 1.300–1.800 linhas
manuais incluindo testes e documentação. Excede o alvo de 800 linhas com
autorização explícita do responsável: adapter do provider, validação JWT,
cookie, antiforgery, pipeline, admissão interna, revogação e testes precisam
entrar juntos para não publicar uma sessão que contorne autorização ou CSRF.
Não há migration.

## Decisões aplicadas

- O BFF chama somente `POST /auth/v1/token?grant_type=password` no servidor,
  com `HttpClient` tipado, chave de publicação configurada, HTTPS/loopback de
  Development e redirects desabilitados. Corpo, senha, e-mail, tokens e erros
  brutos do provider nunca entram em logs ou respostas.
- A resposta do provider precisa passar pela validação JWT existente e pela
  mesma admissão interna usada pelo gate antes de o host criar cookie. O cookie
  contém somente `iss`, `sub`, `session_id`, `iat`, `exp`, `amr` e `aal` de
  prova já validada, é `HttpOnly`, `SameSite=Strict`, `Secure` fora de
  Development, não persistente, sem renovação deslizante e expira exatamente
  no `exp` assinado. Access/refresh token são descartados.
- Policy scheme usa JWT Bearer se existir `Authorization`; sem header usa
  cookie BFF. O cookie preserva `iss`, `sub`, `session_id`, `iat`, `exp`,
  `amr` e `aal`, portanto atravessa o mesmo `InternalSessionGateMiddleware`,
  RLS, auditoria e revogação atuais.
- `GET /foundation/identity/browser-session/antiforgery`,
  `POST /foundation/identity/browser-session` e
  `DELETE /foundation/identity/browser-session` são `private, no-store`.
  Login e toda mutação feita com cookie exigem antiforgery; Bearer existente
  não é alterado. Logout é global VINX, não logout do provider. Não haverá
  CORS permissivo: #16 configurará cliente/proxy same-origin.
- O host usa `__Host-vinx.session` em produção, sem `Domain`, `Expires` ou
  `Max-Age`; Development usa nome próprio compatível com loopback. A chave de
  Data Protection exige anel durável, compartilhado, isolado por aplicação e
  cifrado em produção; sem isso, réplica/rolling deploy falha fechado.
- Login recebe rate limit dedicado por IP confiável e limite global, sem chave
  baseada em e-mail. Credencial inválida, identidade inexistente ou não
  admitida retornam o mesmo `401` vazio, sem cookie.
- Logout de cookie chama a revogação transacional já existente e sempre envia
  limpeza do cookie; indisponibilidade permanece `503` seguro. Refresh,
  sessão persistente, OAuth/SSO, TOTP, recuperação de senha e UI permanecem
  na #44, #16, #90 e #117.

## Tarefas

1. Escrever RED para adapter password grant, validação JWT, flags do cookie,
   seleção Bearer/cookie, antiforgery, respostas seguras, logout/revogação e
   corrida entre logout e prova antiga.
2. Criar adapter Supabase concreto e validador de access token; registrar
   options validadas, `HttpClientFactory` sem redirects/cookies e autenticação
   native policy-scheme/cookie. Não adicionar SDK ou abstração sem segundo
   provider.
3. Mapear endpoints BFF e middleware antiforgery no host; manter handlers
   pequenos, respostas estáveis (`400`, `401`, `429`, `503`) e o gate de
   Foundation como única fonte de `ActorContext`.
4. Atualizar OpenAPI, módulo Foundation, runbook de chave de publicação e
   persistência de Data Protection, mais manifesto BDD de verificação.
5. Fazer revisão Sol, congelar, rodar `./scripts/verify-public.sh` uma vez e
   publicar draft com `gh stack submit` acima de #218.

## Verificação mínima

- integration tests com provider HTTP falso, JWT real de autoridade de teste,
  cookie e PostgreSQL real para gate/revogação;
- testes de ausência de token em cookie/JSON/log e de antiforgery somente em
  fluxos cookie; `Authorization` inválido nunca cai no cookie;
- contrato OpenAPI, configuração fail-fast e `git diff --check`;
- gate público único no estado congelado antes do push.
