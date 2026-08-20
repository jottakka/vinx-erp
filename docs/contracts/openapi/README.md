# Política de contratos OpenAPI

Contratos HTTP são publicados pelo `ApiHost` e documentados por módulo. O
documento OpenAPI descreve comportamento observável, mas não substitui
autorização nem proteções do banco. Em Development, `/openapi/v1.json` não é
publicado em Production. Operações protegidas comuns de Foundation, incluindo
`GET /foundation/bootstrap`, declaram bearer HTTP **ou** cookie de sessão BFF e
as respostas aplicáveis (`200`, `401`, `429` e `503`). O `401` não tem corpo e
o `503` declara somente `{ code: string }`.

Toda resposta declarada pelo `ApiHost` inclui o header obrigatório de resposta
`X-Correlation-ID` (`string`, formato `uuid`). Ele é criado pelo servidor para
suporte e pode acompanhar uma falha sem expor detalhes internos. Nunca é
parâmetro de requisição, não aceita valor do cliente e não representa
autorização, sessão ou idempotência.

As operações BFF de mesma origem são públicas apenas onde indicado:

- `GET /foundation/identity/browser-session/antiforgery`: anônima, `200` com
  `requestToken` ou `503` seguro;
- `POST /foundation/identity/browser-session`: anônima, CSRF obrigatório,
  header obrigatório `X-CSRF-TOKEN`, corpo obrigatório `application/json` com
  somente `email` e `password`, JSON de no máximo 8 KiB contado nos bytes
  efetivamente recebidos, `204`, `400`, `401`, `429` ou `503` seguro; corpo
  maior retorna `400 {"code":"invalid_request"}`;
- `DELETE /foundation/identity/browser-session`: somente cookie BFF e CSRF,
  header obrigatório `X-CSRF-TOKEN`, `204`, `400`, `401`, `403` vazio ou `503`
  seguro.

O logout não declara bearer como alternativa. `Authorization` presente sempre
seleciona bearer; bearer válido no logout somente-cookie recebe `403` vazio sem
revogar sessão ou remover cookie, e valor inválido não pode cair no cookie. O
documento descreve o cookie como mecanismo de segurança, nunca como token de
acesso nem contrato para chamar Supabase pelo browser.

`GET /foundation/platform/bootstrap` segue a mesma publicação bearer e
declara `200`, `401`, `403`, `429` e `503`. A resposta de sucesso contém apenas
`isPlatformOperator`; `403` e `503` expõem somente códigos estáveis de policy,
sem revelar dados de tenant ou de identidade.

`POST /foundation/platform/organizations` publica bearer e declara `201`,
`200`, `400`, `401`, `403`, `409`, `429` e `503`. O corpo possui apenas
`requestId`, `groupName`, `companyName`, `ownerDisplayName` e `ownerEmail`;
campo desconhecido, duplicado ou JSON malformado devolve
`400 {"code":"invalid_request"}`. A autorização confirma primeiro o operador
de plataforma e depois `aal2`: os códigos `platform_operator_required` e
`mfa_assurance_required` não expõem tenant, identidade externa ou o claim
bruto. Criação retorna IDs opacos de grupo, empresa, usuário, membership e
convite com `invitationStatus: "pending_delivery"`; replay retorna o mesmo
corpo com `200`. Conflitos são exclusivamente
`idempotency_conflict` ou `organization_conflict`.

`POST /foundation/companies/{companyId}/employees` publica bearer ou cookie
BFF e declara `201`, `200`, `400`, `401`, `403`, `409`, `429` e `503`.
Quando autenticado pelo cookie BFF, exige o header `X-CSRF-TOKEN`; chamadas
autenticadas por bearer não exigem esse header.
Exige `foundation.employees.create` no escopo `company`; o corpo
`application/json` contém exatamente `requestId`, `companyId`, `displayName` e
`email`, e os dois IDs de empresa devem coincidir. JSON não compatível, campo
desconhecido ou duplicado, e-mail sem sintaxe válida ou corpo maior que 8 KiB
retorna `400 {"code":"invalid_request"}` antes da mutação; o limite conta os
bytes efetivamente lidos, inclusive transferência chunked. Criação retorna
apenas os IDs opacos de employee, membership e user; replay retorna os mesmos
IDs com `200`. Os únicos conflitos publicados são
`idempotency_conflict` e `employee_conflict`; o último não revela se o e-mail
já existe. As respostas desse POST usam `Cache-Control: private, no-store`.

`GET /foundation/companies/{companyId}/employees` usa a mesma autenticação e
capability e declara `200`, `401`, `403`, `429` e `503`. A lista contém
somente `employeeId`, `membershipId` e `displayName` da empresa autorizada; não
publica e-mail, role, identidade externa, convite ou estado de entrega. Todas
as respostas desse GET, inclusive erros, usam `Cache-Control: private, no-store`.

Todo comando recebe contexto explícito de empresa e é autorizado no servidor
por membership, capability nomeada e escopo. A resposta não revela dados de
empresa inacessível. O frontend pode ocultar UI pelas capabilities efetivas,
mas requisição forjada continua negada e, quando a política exigir, auditada.

Versione o contrato quando houver quebra visível ao consumidor. No piloto
interno, prefira campos aditivos e janela de migração a gerações paralelas da
API. Documente capability, ação de auditoria, idempotência e redaction junto a
operações de alto risco.

Não publique documento que omita bearer auth ou anuncie apenas `200` para
operações com `204`, `4xx` ou `503`. Endpoints devem declarar resultados,
schemas e requisitos de segurança verificáveis antes de entrar no contrato.

Bytes de arquivo não trafegam pela rota comum da API. O servidor autoriza URL
privada e curta de upload ou download e registra metadados e evidência de
auditoria.

## Paguai MVP #227

`GET /paguai/requests?companyId={uuid}` e
`GET /paguai/requests/{id}?companyId={uuid}` exigem bearer HTTP ou cookie BFF
de mesma origem. Ambos recebem somente a empresa explícita e devolvem somente
rascunhos do solicitante autenticado. A lista declara `200`, `400`, `401`,
`403`, `429` e `503`; o detalhe declara `200`, `401`, `404`, `429` e `503`.
Detalhe de outro solicitante, outra empresa ou sem permissão é sempre
`404 {"code":"resource_not_found"}`.

`POST /paguai/requests` usa a mesma segurança; para cookie BFF, CSRF exige
`X-CSRF-TOKEN`. O corpo JSON estrito contém exatamente `companyId`, `requestId`,
`title`, `description`, `amount`, `currency`, `dueDate`,
`beneficiaryDisplayName` e `proofRequirement`. `amount` é texto decimal exato
positivo com no máximo duas casas, entre `0.01` e `9999999999999999.99`, sem
arredondamento (por exemplo, `"1250.25"`); `currency` é exatamente `BRL`; `dueDate` usa `YYYY-MM-DD`;
`beneficiaryDisplayName` é aparado, não vazio e limitado a 200 caracteres; e
`proofRequirement` é `optional` ou `required`. Os textos de título e descrição
continuam limitados a 200 e 4000 caracteres. Aceita somente
`Content-Type: application/json` e no máximo
24 KiB de bytes recebidos; mídia incorreta devolve `415` e corpo excedente
devolve `400 {"code":"invalid_request"}`, ambos antes de qualquer mutação. Retorna `201` e `Location` na primeira criação, `200` no
replay idêntico, `400`, `401`, `403`, `409 {"code":"idempotency_conflict"}`,
`429` ou `503 {"code":"paguai_unavailable"}`. A resposta de sucesso contém
`id`, `companyId`, `requestId`, `title`, `description`, `amount`, `currency`,
`dueDate`, `beneficiaryDisplayName`, `proofRequirement`, `state`, `version` e
`createdAt`; `state` é `rascunho` e `version` é `1`. O beneficiário é somente
texto informativo e nunca entra em logs, auditoria ou métricas.

O contrato não expõe payload em logs ou auditoria. Todas as respostas privadas
recebem `Cache-Control: private, no-store` e o header de correlação do host.

## Notifications inbox #94

`GET /notifications/inbox?cursor=<opaco>&limit=<1..50>` e
`GET /notifications/inbox/unread-count` publicam bearer HTTP ou cookie BFF
de mesma origem e declaram `200`, `400 invalid_request` (cursor/limite
inválidos), `401`, `429` e `503 notifications_unavailable`. `POST
/notifications/inbox/{deliveryId}/read` usa a mesma segurança, exige
`X-CSRF-TOKEN` quando autenticado por cookie BFF e declara `200`, `400
invalid_request` (CSRF ausente ou `Guid.Empty`), `401`, `404
resource_not_found`, `429` e `503`. Não há `403`: a inbox é pessoal por
membership ativa, calculada no servidor a cada chamada, e lista/count vazios
não revelam empresas.

A resposta de sucesso contém exclusivamente `items` e `nextCursor` (ou
`unreadCount`); cada item expõe `id`, `companyId`, `source`, `event`,
`createdAt`, `readAt` e `action {kind, href}` derivados de allowlist fechada
por chave completa — chaves desconhecidas produzem `Sistema`/`null`/`null` e
nunca ecoam `event_key`, `deep_link_selector` ou `template_key`. `cursor` é
opaco, versionado e canônico; a UI navega somente por `action.href`
allowlistado. A marcação de leitura é idempotente: repetir o POST devolve o
mesmo `200 {status:"read"}` sem duplicar auditoria. Item inexistente ou fora
do escopo autorizado é sempre `404 resource_not_found`. `401` e `429` não
têm corpo. Todas as respostas, inclusive erros, usam `Cache-Control:
private, no-store` aplicado pelo middleware BFF e o header `X-Correlation-ID`
do host.

## Atendimento / Kanban MVP

`GET /atendimento/cards?companyId={uuid}` aceita bearer ou cookie BFF e exige
`service_desk.ticket.read_queue` no escopo Company. Retorna `200` com cartões
da empresa selecionada, ou `400 invalid_request`, `401`, `403
membership_required|capability_denied`, `429` e `503 atendimento_unavailable`.

`POST /atendimento/cards` exige `service_desk.ticket.create`; o corpo possui
`companyId`, `requestId`, `title` e `description`. Retorna `201`, ou `200` no
replay equivalente; `400 invalid_request`, `401`, `403`, `409
idempotency_conflict`, `429` e `503` são publicados. `POST
/atendimento/cards/{id}/assign` exige `.assign` e recebe também
`expectedVersion` e `assigneeMembershipId`. `POST /atendimento/cards/{id}/move`
exige `.move` e recebe `expectedVersion` e `column` (`novo`, `em_andamento` ou
`concluido`). As duas retornam `200`, e podem retornar `404 resource_not_found`,
`409 idempotency_conflict|integrity_conflict`, `400`, `401`, `403`, `429` ou
`503`. `integrity_conflict` devolve apenas o estado atual já autorizado do
cartão, nunca dados de outra empresa. As três mutações publicam o header
`X-CSRF-TOKEN`: ele é obrigatório quando a operação usa cookie de sessão BFF e
é obtido em `GET /foundation/identity/browser-session/antiforgery`; clientes
bearer não precisam de antiforgery. Todas as respostas mantêm
`X-Correlation-ID`, usam `Cache-Control: private, no-store` e as mutações usam
auditoria transacional.
