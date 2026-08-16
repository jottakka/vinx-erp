# Política de contratos OpenAPI

Contratos HTTP são publicados pelo `ApiHost` e documentados por módulo. O
documento OpenAPI descreve comportamento observável, mas não substitui
autorização nem proteções do banco. Em Development, `GET /foundation/bootstrap`
está publicado em `/openapi/v1.json`: declara bearer HTTP na operação e as
respostas `200`, `401`, `429` e `503`. O `401` não tem corpo e o `503` declara
somente `{ code: string }`. O documento não é publicado em Production.

`GET /foundation/platform/bootstrap` segue a mesma publicação bearer e
declara `200`, `401`, `403`, `429` e `503`. A resposta de sucesso contém apenas
`isPlatformOperator`; `403` e `503` expõem somente códigos estáveis de policy,
sem revelar dados de tenant ou de identidade.

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
