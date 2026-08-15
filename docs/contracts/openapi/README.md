# Política de contratos OpenAPI

Contratos HTTP são publicados pelo `ApiHost` e documentados por módulo. O
documento OpenAPI descreve comportamento observável, mas não substitui
autorização nem proteções do banco. A publicação começa em #110 junto do
primeiro contrato tipado consumido pelo frontend.

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
