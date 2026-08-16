# Provisionamento interno de organização e dono inicial — plano curto #208

**Objetivo:** permitir que o operador global, com segundo fator validado pelo
Supabase, crie de forma atômica um grupo, uma empresa inicial e o dono local
pendente de ativação pelo provedor.

**Limite:** previsão de 22–25 arquivos e aproximadamente 850–1.050 linhas
manuais; migration designer, snapshot de schema e lockfiles gerados são
reportados separadamente. A exceção ao alvo de 800 linhas é coesa: a mesma
fronteira precisa de JWT/AAL, policy, função PostgreSQL, RLS/grants,
idempotência, auditoria e rota testáveis juntos. Aceite/provider, entrega
externa e UI continuam nas PRs #211 e #212.

## Decisões aplicadas

- A rota é `POST /foundation/platform/organizations`, protegida por operador
  global e `aal2` do JWT Supabase já validado. `aal2` é a prova de segundo
  fator do provider; esta fatia não implementa enrollment, recuperação ou UI
  de TOTP.
- O corpo aceita somente `requestId`, `groupName`, `companyName`,
  `ownerDisplayName` e `ownerEmail`. IDs, escopo de empresa, role, grants,
  issuer e subject nunca vêm do navegador. A resposta expõe somente IDs
  opacos e `pending_delivery`.
- Uma função `SECURITY DEFINER` de owner PostgreSQL `NOLOGIN/NOBYPASSRLS`
  revalida operador e AAL dentro da transação. Ela cria grupo, empresa, User
  local sem `ExternalIdentity`, membership, role fixa `owner`, grants
  allow-list, assignment, convite pendente, recibo global de idempotência e
  auditoria global. O operador não recebe membership.
- A role `owner` recebe somente as capabilities atuais
  `foundation.roles.assign`, `foundation.memberships.revoke`,
  `foundation.files.upload` e `foundation.files.confirm`; nunca recebe
  `platform.operator`, AI ou permissões financeiras futuras.
- Nomes de grupo/empresa e e-mail de usuário são tratados como únicos sem
  distinção de caixa neste MVP. Company deixa de ser única apenas dentro do
  grupo, pois representa a filial inicial.
- O convite não contém token, issuer, subject, provider state ou alegação de
  entrega. #211 deverá vincular uma identidade verificada ao dono já criado,
  sem duplicar a membership/role, e tratar funcionários genéricos e aceite.

## Tarefas

1. Escrever RED para AAL do provider, policy `aal2`, contrato HTTP, DDL/RLS,
   grafos atômicos, rollback, unicidade e replay concorrente.
2. Propagar somente o nível AAL normalizado da prova Supabase para
   `ActorContext` e para a GUC transacional; manter ausência como `aal1` e
   valor ambíguo/desconhecido fail-closed.
3. Criar migration SQL com tabelas técnicas de convite/recibo, owner mínimo,
   grants/RLS/função qualificada e índices normalizados; alterar a unicidade
   da empresa no modelo e regenerar artefatos de schema.
4. Criar serviço estreito que chama a função e a rota minimal API, com
   mapeamento seguro para `201`, replay, `403`, `409` e `503`.
5. Atualizar Foundation, runbook, OpenAPI e manifesto BDD; fazer revisão Sol,
   congelar código, executar o gate público uma vez e publicar como draft na
   stack.

## Verificação mínima

- unit tests do parser AAL e integration tests de policy/endpoint;
- migration, RLS, grants, `search_path`/temp-shadow e snapshot em PostgreSQL
  real;
- testes de serviço para grafo, auditoria transacional, idempotência,
  conflitos e concorrência;
- `./scripts/verify-public.sh` somente no estado congelado imediatamente
  antes de `gh stack submit`.
