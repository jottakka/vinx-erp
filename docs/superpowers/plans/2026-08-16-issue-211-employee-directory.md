# Funcionário local sem login — primeira fatia #211

**Objetivo:** permitir que uma pessoa já autorizada em uma empresa cadastre um
funcionário mínimo sem criar acesso externo, login, papel, convite ou entrega.

**Limite:** previsão de 16–20 arquivos e aproximadamente 650–780 linhas
manuais; migration designer e snapshots gerados são reportados separadamente.
Esta primeira PR é deliberadamente menor que o issue #211 completo: o fluxo
de provider, entrega e aceite depende de outbox/worker, reconciliação com
Supabase e uma extensão segura da sessão BFF. Misturá-los criaria uma fronteira
incompleta e não operável.

## Decisões aplicadas

- A capability nova e estreita é `foundation.employees.create`, no escopo
  `company`. Ela é concedida somente à role inicial que já possui
  `foundation.roles.assign` no escopo de empresa, preservando o acesso do dono
  criado pela #208 sem conceder acesso ao operador global.
- O comando aceita somente `requestId`, `companyId`, `displayName` e `email`.
  O servidor deriva ator, membership e autorização; não aceita IDs de usuário,
  membership, role, grant, issuer ou subject.
- Uma função PostgreSQL `SECURITY DEFINER`, de owner `NOLOGIN/NOBYPASSRLS`,
  revalida ator, membership ativa, capability e empresa dentro da transação.
  Ela cria `User`, `Membership` e `Employee`, recibo por empresa e auditoria
  de sucesso de forma atômica.
- A criação é somente uma reserva local: não há `ExternalIdentity`, role
  assignment, capability grant, token, convite, outbox ou chamada de provider.
  Por isso `aal1` é suficiente nesta fatia; qualquer vínculo que possa liberar
  acesso externo voltará a exigir `aal2` na PR de aceite.
- E-mail não serve para vincular contas existentes. Conflito de e-mail não
  reutiliza `User` e não expõe detalhes sensíveis. O raw email não entra em
  logs, auditoria ou resposta; o diretório devolve somente IDs opacos e nome.
- A rota de aceite continua inexistente. Identidade externa desconhecida segue
  recebendo `401` pelo gate normal.

## Stack planejada

1. **#211A (esta PR):** diretório e reserva local sem login.
2. **#211B (depois de #26/#48 ou spike equivalente):** convite provider e
   outbox/reconciliação, sem alegar entrega antes da confirmação do worker.
3. **#211C (depois de BFF de aceite e MFA #18):** aceite por identidade
   exata, consumo condicional, `ExternalIdentity` e sessão segura.

## Verificação mínima

- testes RED/GREEN para contrato HTTP, capability, idempotência e ausência de
  identidade externa/role/invite;
- PostgreSQL real para função, RLS/grants, auditoria transacional, unicidade e
  concorrência;
- isolamento entre empresas e negativa auditada;
- revisão independente antes do freeze e `./scripts/verify-public.sh` uma vez
  imediatamente antes do push.
