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
`title` e `description`; textos são aparados, não vazios, e limitados a 200 e
4000 caracteres. Retorna `201` e `Location` na primeira criação, `200` no
replay idêntico, `400`, `401`, `403`, `409 {"code":"idempotency_conflict"}`,
`429` ou `503 {"code":"paguai_unavailable"}`. A resposta de sucesso contém
apenas `id`, `companyId`, `requestId`, `title`, `description`, `state`,
`version` e `createdAt`; `state` é `rascunho` e `version` é `1`.

O contrato não expõe payload em logs ou auditoria. Todas as respostas privadas
recebem `Cache-Control: private, no-store` e o header de correlação do host.
