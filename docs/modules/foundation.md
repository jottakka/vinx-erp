# Módulo Foundation

Foundation fornece os contratos transversais do ERP: identidade, empresas,
membership, capabilities, RLS, auditoria, idempotência e as aprovações
duráveis consumidas pelos módulos. Não contém regras de negócio de Paguai,
Service Desk, Kanban, HR, Notificações ou AI.

## Ownership

- grupos, empresas, unidades, times, usuários e memberships ativas;
- employees e fronteiras dos provedores de identidade e armazenamento:
  `IdentityProvider` e `FileStorage`;
- roles, capabilities nomeadas e escopos `self`, `team`, `unit`, `company` e
  `group`;
- políticas de aprovação configuráveis e alçadas; aprovador único é
  configuração de dados, nunca regra fixa do módulo;
- `foundation.approvals`, seus serviços de preparação, confirmação e veto;
- identidade, auditoria, correlação, redaction, telemetria, health checks,
  idempotência e contratos técnicos de outbox.

## Company scope

Todo comando recebe uma `empresa_id` explícita e o servidor resolve o ator por
contexto autenticado. A UI apenas molda menus; API, MCP e PostgreSQL negam
contexto forjado ou cruzado. Em aprovações, RLS é forçado e combina empresa e
ator; contexto ausente falha fechado.

## Catálogo de capabilities

`foundation.capabilities` é catálogo técnico administrado exclusivamente por
migrations. Os nomes canônicos são identidade estável; o runtime apenas lê o
catálogo e não recebe `INSERT`, `UPDATE` ou `DELETE`. A migration preserva uma
linha legada que já use o mesmo nome, falha se o UUID canônico estiver ocupado
por outro nome e não permite downgrade que remova uma capability referenciada
por um grant. Para comandos atuais, somente grant de escopo `company` satisfaz
o requisito; os demais escopos falham fechados até existir regra específica.

## Identidade autenticada

A API valida o JWT do Supabase Auth e resolve o ator interno exclusivamente
pelo par imutável `issuer` + `subject` em `foundation.external_identities`.
E-mail não participa da resolução e uma identidade externa desconhecida recebe
`401`; a API não cria `User` nem `ExternalIdentity` durante uma requisição.
O runtime possui somente `SELECT` nessa tabela para essa consulta.
Antes dessa resolução, `session_id` deve ser UUID e `amr` deve conter
autenticação não `token_refresh`; ausência ou malformação falha fechada com
`401`. A prova não persiste token nem dado pessoal.

`InternalSessionAdmissionService` é usado pelo gate HTTP: bloqueia o usuário
por função SQL, grava uma sessão EF só quando a autenticação é estritamente
posterior à barreira e registra auditoria global na mesma transação. Repetição
ativa não cria auditoria; barreira, revogação e colisão produzem negação
auditada sem identificador externo. O gate cria o `ActorContext` com o `User`
interno e o ID da linha de identidade externa resolvida, ambos derivados no
servidor, e decide entre `401` e `503` antes do endpoint protegido.

`GET /foundation/memberships` usa somente esse `ActorContext`. Em uma única
transação de leitura, descobre as memberships ativas do ator e projeta as
capabilities efetivas por empresa, em ordem de nome da empresa. Cada empresa
recebe contexto RLS próprio antes da leitura de grants; uma membership válida
sem grant retorna `capabilities: []`. A resposta não expõe papéis, identidade
externa ou dados de autenticação. Falha de descoberta retorna `503` seguro.
`GET /foundation/capabilities?companyId=` continua sendo o contrato de uma
única empresa e preserva suas negações auditáveis.

`GET /foundation/bootstrap` também usa somente o `ActorContext` criado pelo
gate. Entrega o perfil mínimo da própria pessoa (`id` e `displayName`), suas
memberships ativas ordenadas com capabilities efetivas e `flags` como mapa
`string -> bool`. Não recebe corpo, usuário, empresa, capability ou flag do
cliente; não expõe papel, e-mail, issuer, subject, token, sessão ou empresa
não autorizada. Pessoa admitida sem membership recebe `companies: []`.

`401` vazio é decidido pelo authentication/session gate antes do handler.
Indisponibilidade do gate retorna somente
`503 {"code":"identity_gate_unavailable"}`; indisponibilidade na leitura de
perfil ou memberships retorna somente `503 {"code":"audit_unavailable"}`.
Não há resposta parcial e cancelamento propaga. As flags são validadas no
startup e materializadas para o processo: mudança de configuração exige novo
deploy ou restart.

## Sessão Browser BFF

A mesma origem usa `GET /foundation/identity/browser-session/antiforgery`,
`POST /foundation/identity/browser-session` e
`DELETE /foundation/identity/browser-session`. O login recebe credenciais e
emite somente a prova `HttpOnly`, `SameSite=Strict`, de caminho `/` e sem
persistência; fora de Development também exige `Secure`. O cookie é
`vinx.session` em Development e
`__Host-vinx.session` nos demais ambientes. Access token, refresh token,
e-mail e resposta bruta do provedor nunca chegam ao browser. O browser não
chama Supabase, Storage ou PostgreSQL diretamente.

O login exige `X-CSRF-TOKEN` e corpo `application/json` contendo somente
`email` e `password`. O JSON tem no máximo 8 KiB, incluindo espaços em branco
e contando os bytes efetivamente recebidos, sem confiar em `Content-Length`.
Excesso retorna `400 {"code":"invalid_request"}` antes de chamar o provedor
ou emitir cookie de sessão.

Operações protegidas comuns aceitam bearer válido ou essa prova de cookie. A
presença de `Authorization`, inclusive malformado, seleciona bearer e nunca
faz fallback para cookie. O logout aceita somente cookie: `DELETE` exige CSRF
válido e revoga globalmente as sessões da própria pessoa. Após CSRF válido, a
prova local é removida uma única vez também quando o gate encerra a requisição
com `503 {"code":"identity_gate_unavailable"}`; isso não autoriza ator nem
transforma indisponibilidade em sucesso. CSRF ausente ou inválido, pedido
anônimo e `Authorization` inválido não removem cookie. Bearer válido no logout
somente-cookie retorna `403` vazio, sem remover a prova local nem revogar
sessões.

As rotas BFF usam `Cache-Control: private, no-store`. CSRF e login público
compartilham a origem, mas somente o endpoint `POST` de login entra nos
limites: 100 requisições por minuto globais e 10 por minuto por IP; aliases
aceitos pelo roteamento compartilham o mesmo limite global. A emissão de token
antiforgery não consome esses limites. A entrega não amplia #44 com JIT por
e-mail, MFA, recuperação ou fluxos adicionais de provedor, nem entrega #79:
listagem ou revogação individual de sessões continuam adiadas.

O operador de plataforma é um registro global singleton em
`foundation.platform_operators`, separado de company, membership, role e
capability de tenant. A policy `platform.operator` consulta somente a função
SQL `current_actor_is_platform_operator()` usando o ID exato de identidade
externa do `ActorContext`, aplicado como `app.external_identity_id` somente na
transação do servidor; ausência desse ID nega acesso. Papel de tenant, claim
JWT, metadado do provedor e outra identidade ligada ao mesmo `User` nunca
concedem esse acesso. A negação da policy registra
`platform.operator.required` em auditoria global e retorna somente `403` com
code `platform_operator_required`. Indisponibilidade da função ou da auditoria
falha fechada com `503` e code
`platform_operator_unavailable`.

`GET /foundation/bootstrap` inclui a projeção aditiva
`platform.isPlatformOperator`, sem e-mail, issuer, subject, papel ou token.
`GET /foundation/platform/bootstrap` exige a mesma policy e retorna somente
`isPlatformOperator: true`; ele não lê dados de empresa. A projeção orienta a
interface, mas toda mutação futura continua reavaliando o operador no banco.

`POST /foundation/platform/organizations` é a única criação inicial de
organização nesta fatia. Exige primeiro o operador global exato e, somente
depois, a assurance normalizada `aal2`; quem não é operador recebe
`platform_operator_required` sem conhecer seu estado de MFA. Um operador
confirmado com `aal1` recebe `mfa_assurance_required` e a negação é auditada
globalmente. A assurance é derivada do claim pelo gate, mantida como enum no
`ActorContext` e aplicada como `app.mfa_assurance`; o claim bruto não é
persistido nem registrado.

O corpo HTTP contém `requestId`, `groupName`, `companyName`,
`ownerDisplayName` e `ownerEmail`. O servidor normaliza os campos, calcula o
hash SHA-256 canônico da representação UTF-8 do array JSON ordenado desses
cinco valores e passa somente esse hash à função SQL. O recibo de
idempotência armazena IDs opacos e o hash, nunca dados pessoais. Repetição do
mesmo ator, request e hash devolve os mesmos IDs; payload diferente devolve
`idempotency_conflict`. Nomes ativos de grupo e empresa, e e-mail de usuário,
são únicos globalmente por `lower(btrim(...))`.

A função `SECURITY DEFINER` pertence a `vinx_provisioning_owner`, role
`NOLOGIN` sem Bypass RLS. Ela serializa o par ator/request, cria grupo,
empresa, usuário local, membership, role fixa `owner`, quatro grants
necessários, assignment e convite técnico `pending_delivery`; então grava
recibo e auditoria global `platform.organization.provision` na mesma
transação. Antes do commit, o serviço acrescenta ao papel `owner` a capability
`foundation.employees.create` pela função estreita do diretório, vinculada ao
recibo exato daquele operador, request, empresa e membership; o operador
continua sem membership ou acesso ao tenant criado. Não há envio ou aceite no
provedor durante essa criação HTTP, employee criado para o owner, interface ou
fluxo de aceite nesta fatia.

### Outbox transacional do convite inicial

Quando o resultado é `created`, a mesma transação persiste uma única mensagem
durável `OwnerInvitationDeliveryRequested`. O evento contém somente o UUID
opaco `invitationId`: nome, e-mail, token, corpo HTTP, hash e segredo não
entram no evento, nos metadados do envelope nem nos logs. `replay`, negações e
conflitos não criam mensagem.

Negócio, recibo de idempotência, auditoria e envelope pertencem à mesma
transação, cujo commit é único no serviço. O flush acontece apenas depois de
confirmação do commit. Falha antes do commit reverte todas essas gravações;
resultado ambíguo do commit não é compensado nem republicado; falha isolada de
flush conserva o convite como `pending_delivery` e o envelope durável para
recuperação posterior pelo relay.

O runtime usa a única conexão `FOUNDATION_RUNTIME_CONNECTION` tanto para
Foundation quanto para Wolverine. O storage técnico fica em
`vinx_wolverine` e `vinx_wolverine_queues`, sem confundir esses schemas com as
migrations ou snapshot de Foundation. O ApiHost produtor não cria DDL nem tem
ownership desses recursos; o provisionamento é etapa explícita de deploy. O
WorkerHost consome a fila durável no mesmo banco físico, mas usa a conexão
separada `FOUNDATION_WORKER_CONNECTION`, o envelope próprio em
`vinx_wolverine_worker` e somente a fila compartilhada necessária.

O único handler explícito do Worker chama a função SQL estreita de ingresso e
altera o convite de `pending_delivery` para `pending_provider`, com uma única
auditoria `owner_invitation.delivery.ingress`; replay não duplica a transição
nem a auditoria. A #211B então reivindica uma tentativa sob lock: somente o
vencedor lê o e-mail em memória, chama `POST /invite` do Supabase e grava
o resultado por outra função `SECURITY DEFINER`, fora de transação aberta no
provedor. Sucesso validado pelo `issuer` e `subject` retorna
`pending_acceptance`; rejeição definitiva do provider retorna `provider_failed`;
timeout, erro de autenticação/rate limit, 5xx, redirect ou corpo inválido
retornam `provider_ambiguous`, sem retry cego. Repetição enquanto a tentativa
está em curso preserva `provider_in_flight`, sem nova chamada. Token, e-mail,
corpo HTTP e segredo não
persistem em envelope, log ou auditoria. O worker não recebe DML direto em
Foundation: as funções estreitas são sua única fronteira de negócio. Aceite,
login da identidade reservada e reconciliação de estado ambíguo permanecem
fatias posteriores.

`provider_in_flight` sem finalização — por queda do worker entre a
reivindicação e a chamada, ou por falha transitória após o provider aceitar —
permanece seguro: não há segunda chamada e o replay encerra sem mutação. A
recuperação operacional fica para a reconciliação da #211B; até lá, o runbook
é auditar `owner_invitation.delivery.provider` e `provider_attempt_id` da
tentativa, confirmar com o provider se o convite foi criado e, se necessário,
emitir nova intenção de provisionamento — nunca reabrir o estado manualmente.

O destino do convite (`SUPABASE_INVITE_REDIRECT_URL`) é validado no startup
como URI HTTPS absoluta sem fragmento e, quando
`SUPABASE_INVITE_REDIRECT_ALLOWED_ORIGINS` está definido (lista de origens
absolutas separadas por `;`), a origem do redirect precisa pertencer à lista.
O cliente HTTP não segue redirects, então a service-role key nunca é
reenviada a outra origem.

O startup falha fechado se a conexão não for efetivamente `vinx_worker`; não
cria DDL em runtime e descobre somente esse handler. Erros transitórios de
PostgreSQL têm tentativas agendadas finitas. Erro permanente, inesperado ou
tentativas esgotadas seguem para a DLQ do worker com UUID zerado, sem headers,
payload original, dados pessoais ou mensagem interna de exceção.

## Diretório local de funcionários

`POST /foundation/companies/{companyId}/employees` reserva uma pessoa no
diretório local. O corpo estrito `application/json` contém somente `requestId`,
`companyId`, `displayName` e `email`, e o identificador do corpo deve coincidir
com a empresa da rota. A operação exige identidade interna conhecida,
membership ativa e a capability `foundation.employees.create` em escopo
`company`; a função SQL reavalia as três condições antes de consultar a
idempotência. O corpo é limitado a 8 KiB pelos bytes efetivamente lidos,
inclusive em transferência chunked; JSON inválido ou e-mail sem sintaxe válida
retorna `400 {"code":"invalid_request"}` antes da mutação.
Quando autenticado pelo cookie BFF, o POST exige `X-CSRF-TOKEN`; chamadas
autenticadas por bearer não exigem esse header.

A criação transacional insere somente um `User` novo, uma `Membership` nova e
um `Employee` da empresa, além de recibo e auditoria
`foundation.employee.create`. Ela não cria `ExternalIdentity`, role assignment,
convite, token, outbox ou chamada ao provedor. E-mail é normalizado apenas para
a unicidade global do `User`; nunca volta na resposta, no diretório, na
auditoria ou no log. Colisão de e-mail retorna `employee_conflict` sem revelar
o cadastro existente.

O mesmo ator, empresa, `requestId` e payload retorna os mesmos IDs opacos sem
duplicar linhas nem auditoria. Payload divergente retorna
`idempotency_conflict`. Negação de capability gera auditoria compacta separada;
se essa evidência não puder ser gravada, a API retorna `503` seguro em vez de
confirmar uma negação sem rastro. `GET /foundation/companies/{companyId}/employees`
requer a mesma capability e devolve apenas IDs opacos e `displayName` da própria
empresa. Ambas as rotas do diretório usam `Cache-Control: private, no-store`,
inclusive para respostas de erro produzidas pelo pipeline.

Esta etapa é deliberadamente uma reserva sem login. Entrega pelo provedor,
outbox/worker, token com expiração, vínculo exato de `issuer` + `subject` e
aceite browser permanecem nas próximas fatias da issue #211; identidade
desconhecida continua recebendo `401` fora de um futuro fluxo de aceite
explicitamente limitado.

O único bootstrap inicial é o comando de deployment
`Vinx.Erp.PlatformBootstrap`, sem argumentos. Ele usa a conexão migradora e
configuração de deployment para consultar `GET /auth/v1/admin/users/{id}` no
Supabase com credencial exclusiva de servidor, confirma sujeito, audiência,
e-mail confirmado, identidade não anônima e estado não banido/removido, e só
então chama a função SQL exclusiva do migrador. O comando nunca usa
`user_metadata`, não cria operador por HTTP, não imprime identificador,
e-mail, chave, conexão ou corpo do provedor; sua saída é uma categoria estável.
Reexecução do mesmo issuer e subject devolve `already_configured`, enquanto
outro sujeito falha em conflito sem anexar identidade por e-mail.

O resolver interno de contexto parte somente do `ActorContext` e materializa
um snapshot sem filtro de memberships ativas, nome legível e capabilities por
empresa. Um `CapabilityRequirement` de escopo `company` deriva desse snapshot
o conjunto exato de empresas autorizado para leitura; a aplicação RLS usa
esse conjunto apenas durante a transação atual. O conjunto multiempresa não
participa de `INSERT`, `UPDATE`, `DELETE` nem de `WITH CHECK`: comandos
continuam usando `app.company_id`, e revogação só aparece no próximo snapshot.
Falha de identidade, setup ou banco não produz conjunto parcial; a operação
falha fechada e registra somente contexto operacional seguro.

`RevokeMembershipService` é um serviço interno de aplicação, registrado no
host sem rota HTTP, MCP ou interface. O comando traz empresa, membership e
`request_id`; exige membership ativa do ator e a capability
`foundation.memberships.revoke` exatamente em escopo `company`. A autorização
é sempre avaliada antes da idempotência. Depois dela, um replay com o mesmo
payload vence a busca da membership já excluída e devolve o sucesso original;
payload diferente retorna `idempotency_conflict` e novo `request_id` para a
membership revogada retorna `resource_not_found`. O alvo de outra empresa não
é revelado. A transição faz soft-delete, idempotência e auditoria
`foundation.memberships.revoke` na mesma transação; falha de auditoria desfaz
tudo. A operação registra a métrica segura `membership_revocation` quando a
auditoria ou a transação ficam indisponíveis. Ela não revoga sessão nem altera
as memberships de outras empresas.

`POST /foundation/identity/sessions/revoke-all` não recebe corpo, usuário,
empresa nem alvo. Ele usa somente o `ActorContext` estabelecido pelo gate e
revoga todas as sessões da própria pessoa pela função transacional existente.
`Revoked` e `NoChange` retornam `204`; indisponibilidade retorna
`503 application/json` com objeto de uma única propriedade `code` e valor
`identity_gate_unavailable`. Listagem e revogação individual (#79), interfaces
de sessão (#80), administração de sessão (#81), MFA/AAL (#18), recuperação
(#19) e logout/banimento no provedor (#20) continuam adiados.

## Aprovações duráveis

`PrepareApprovalService` exige membership ativa e
`ai.writes.confirm` exatamente em escopo `company`. Depois do parsing
estrutural, grava proposta, idempotência e auditoria na mesma transação. Replay
do mesmo `RequestId` e payload devolve a proposta sem auditoria duplicada;
payload diferente retorna `idempotency_conflict`; uma segunda proposta viva do
mesmo ator/empresa retorna `proposal_conflict` auditado.

`ConfirmApprovalService` rederiva o hash canônico (chaves ordenadas
recursivamente, números pela representação JSON, SHA-256 hexadecimal minúsculo
de 64 caracteres) e exige a mesma policy version. O claim é um `UPDATE`
condicional único com `now()` do PostgreSQL, `consumed_at IS NULL`,
`deleted_at IS NULL` e `expires_at > now()`. O resultado é `Claimed`; o mesmo
`RequestId` recebe `Replay`; divergência, perda de claim ou novo pedido após
consumo recebem `Conflict`; linha invisível, expirada ou vetada recebe
`ProposalUnavailable` conforme a reconciliação.

Cada confirmação, inclusive replay, reavalia membership/capability e RLS.
Veto também reavalia autorização em cada chamada e faz soft-delete terminal
com `deleted_reason = 'approval_vetoed'`, `request_id` do veto e auditoria.
Replay de veto é `AlreadyVetoed`; veto após consumo é conflito. Falha de
auditoria faz rollback; a mutação de negócio e sua evidência não se separam.

Os `RequestId` de preparação e confirmação são distintos. Um futuro serviço de
execução deverá ter idempotência própria, mas ainda não existe costura com o
harness. Foundation não promete execução exatamente-uma-vez.

## Invariantes persistentes

A migration declara, com nomes estáveis:

- constraints `ck_approvals_expires_after_creation`,
  `ck_approvals_payload_hash_format`, `ck_approvals_veto_complete`,
  `ck_approvals_consumption_complete` e `ck_approvals_terminal_exclusive`;
- índice único parcial `ix_approvals_company_id_actor_user_id`, filtrado por
  `consumed_at IS NULL AND deleted_at IS NULL`, garantindo uma proposta viva
  por ator/empresa;
- índices `ix_approvals_actor_user_id`,
  `ix_approvals_confirmation_request_id` (único parcial, somente quando o
  valor não é nulo) e
  `ix_approvals_proposal_id` (único);
- índice de idempotência
  `ix_idempotency_records_company_id_actor_user_id_command_name_r`.

`ApprovalUniqueViolationClassifier` só classifica os dois nomes pinados
(`ix_approvals_company_id_actor_user_id` como `LiveProposal` e
`ix_idempotency_records_company_id_actor_user_id_command_name_r` como
`Idempotency`); qualquer outro constraint é `Other` e não vira um conflito
inventado.

A preparação lê o relógio da transação, expira propostas vencidas antes de
testar conflito vivo e registra `approval_expired`; a janela de sweep usa o
instante inicial da transação. Expiração, veto e consumo são terminais e
mutuamente exclusivos. A Foundation mantém a linha terminal para replay e
auditoria; a purga física continua fora desta fatia, na issue #201.

## Contract version

`1.5`

## Modelo de entidades e dados

Entidades mutáveis compartilham identidade, auditoria, exclusão lógica e
versão de concorrência por classes-base do domínio, sem dependência do EF. O
mapeamento permanece em `Persistence`, e somente entidades que pertencem a uma
empresa implementam `ICompanyScoped`.

Dados de negócio e configuração usam soft-delete com `deleted_at`,
`deleted_by_user_id` e `deleted_reason`; restauração exige autorização e
auditoria. Eventos de auditoria são append-only e usam retenção, não
soft-delete. Dados de módulo permanecem explicitamente escopados por empresa;
uma visão autorizada de grupo é união de empresas, nunca registro sem tenant.

Cada evento de auditoria possui `scope`: `company` exige
`requested_company_id`, enquanto `global` exige que ela seja nula. O runtime
insere eventos somente por `audit.append_event`; o append de sucesso participa
da mesma transação da mutação de negócio.

## Deletion strategy

Dados de negócio usam soft-delete auditável. Em `foundation.approvals`, veto e
expiração marcam o motivo terminal e consumo grava resultado; nenhum caminho de
runtime recebe `DELETE`. Retenção/limpeza lógica do estado terminal é distinta
da purga física futura da issue #201.

## Migration notes

`AddFoundationApprovals` cria `foundation.approvals`, RLS forçado, policy de
isolamento por empresa/ator, grants mínimos e as constraints/índices acima.
`AddAiWritesConfirmCapability` semeia `ai.writes.confirm` com identidade
estável e impede downgrade enquanto houver grants.

A fronteira de ownership usa `vinx_migrator LOGIN NOINHERIT NOBYPASSRLS`,
`vinx_runtime LOGIN NOINHERIT NOBYPASSRLS`, `vinx_audit_owner NOLOGIN
NOINHERIT NOBYPASSRLS`, `vinx_identity_owner NOLOGIN NOINHERIT NOBYPASSRLS` e
`vinx_authorization_owner NOLOGIN NOINHERIT NOBYPASSRLS`. O owner de identidade
possui somente `USAGE` em `foundation`, `SELECT` em `foundation.users` e
`UPDATE` apenas em `sessions_valid_after`; o owner de autorização recebe
somente leitura necessária para `current_actor_memberships()`. Essas roles são
distintas das roles legadas do Supabase: `anon`, `authenticated` e
`service_role`.

O `vinx_platform_owner NOLOGIN NOINHERIT NOBYPASSRLS` possui somente as
colunas e funções necessárias para manter o singleton de operador de
plataforma. O runtime não é seu membro, não recebe `SELECT` nem DML direto
nessa tabela, e só pode executar a função de consulta do ator atual. A função
de bootstrap permanece exclusiva da conexão migradora. O singleton fixa a
`external_identity_id` do primeiro par `issuer` + `subject` verificado; outra
identidade ligada ao mesmo `User` também recebe conflito. As funções de
segurança usam relações qualificadas e `pg_temp` explícito ao fim do
`search_path`; o appender de auditoria recebe a mesma proteção para que a
evidência transacional não possa ser desviada por uma tabela temporária.

`vinx_provisioning_owner NOLOGIN NOINHERIT NOBYPASSRLS` possui somente as
permissões transitórias e relações necessárias para a função de provisionamento
de organização. O runtime não recebe leitura nem DML nas tabelas de convite e
recibo, e executa apenas a função estreita; `PUBLIC` e migrador não a executam.

`vinx_employee_directory_owner NOLOGIN NOINHERIT NOBYPASSRLS` possui somente
as colunas, policies RLS e funções necessárias para reservar o grafo local de
funcionário e acrescentar a capability inicial ao papel `owner`. O runtime não
recebe DML direto em `users`, `memberships`, `employees`, idempotência ou
grants; executa apenas as duas funções estreitas. O downgrade falha fechado se
existir recibo de criação de funcionário ou grant ativo da capability, para não
remover acesso ou estado observado.

A revogação de membership marca somente soft-delete e auditoria técnica da
membership própria. O runtime recebe `UPDATE` apenas nas colunas de ciclo de
vida da revogação, auditoria de alteração e `row_version`; `INSERT`, `DELETE`
e chaves da membership continuam negados, sob a policy RLS de empresa.

O runtime possui `SELECT` e `INSERT` em nível de tabela e `UPDATE` somente nas
colunas de ciclo de vida, auditoria de alteração, `row_version` e soft-delete;
`DELETE` e alteração de chaves permanecem negados. O contrato de schema é
verificado em PostgreSQL real pelos testes de migração e de grants.

## Observabilidade e segurança

Todos os hosts configuram `AddVinxLogging`, que preserva `ILogger<T>` nativo e
emite console JSON com timestamp UTC ISO 8601. Hosts HTTP usam
`UseVinxRequestCorrelation` antes dos endpoints; o middleware gera o
`CorrelationId` no servidor, marca a `Activity` atual e o propaga por scope.
No `ApiHost`, toda resposta concluída também devolve
`X-Correlation-ID` com esse UUID em formato canônico, inclusive sucesso,
negação segura e exceção tratada. O cabeçalho é referência diagnóstica opaca:
o servidor ignora qualquer valor enviado pelo cliente e ele não concede acesso,
não é chave de idempotência nem contém dado pessoal.
Auditoria é append-only, transacional com a mutação e redigida. Logs usam
templates constantes, operação, módulo, resultado e motivo seguro; nunca
registram tokens, credenciais, PII, prompts ou payload bruto. O host autentica
antes de criar contexto confiável, mas endpoints de aprovação do AI ainda não
fazem parte desta fatia.

Configuração obrigatória de banco, Supabase Auth e Storage usa Options com
`ValidateOnStart`; erro impede o host de servir tráfego sem incluir credencial
na mensagem. HTTP de provider só é aceito em loopback durante desenvolvimento;
demais ambientes exigem HTTPS. Os três hosts usam Health Checks nativo. O
`ApiHost` usa Problem Details somente para exceções não tratadas, preservando
os contratos `{ code }` e `401` vazio. O `McpHost`
reutiliza a validação JWT, mas a sonda `vinx_ping` não materializa ator nem
empresa; ferramentas de domínio continuam bloqueadas até o contexto confiável.
