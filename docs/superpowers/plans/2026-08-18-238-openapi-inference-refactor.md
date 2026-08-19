# OpenAPI por inferência — plano curto #238/#239

**Objetivo:** remover a injeção manual de requestBody/schema dos transformers do
ApiHost (blocos por endpoint + marcadores `*OpenApiMetadata` e
`PaguaiJsonRequestBodyMetadata`) e deixar o documento ser gerado por inferência
dos models anotados ([Required]/[MaxLength]/[Range]/[NonEmptyGuid]), mantendo o
contrato pinado pelos testes IDÊNTICO (diff zero nos pontos verificados).

**Estimativa:** ~12 arquivos, ~250–350 linhas manuais líquidas (remoção líquida
de ~170 linhas do Program.cs); sem migration, sem dependência nova.

**Verificações empíricas (feitas):** no ASP.NET Core 10.0.10 o gerador nativo já
infere `required` via [Required] (modifier `IsRequired`), `maxLength`/minLength
via `ApplyValidationAttributes`, `format: uuid` para Guid e `additionalProperties`
fica ausente (o pipeline de schema não herda `UnmappedMemberHandling.Disallow`).
Conclusão: só o `additionalProperties: false` precisa de um schema transformer
genérico orientado pelas opções de serializer.

1. Remover de Program.cs os 3 blocos manuais (sign-in, employee create, paguai) e
   os marcadores `BrowserBffSessionSignInOpenApiMetadata`,
   `EmployeeDirectoryCreateOpenApiMetadata`, `PaguaiJsonRequestBodyMetadata`;
   endpoints que fazem parse manual declaram o tipo do corpo via
   `JsonRequestBodyContract` (metadata de documento, sem mudar binding).
2. Um transformer de operação genérico de corpo JSON estrito: request bodies
   inferidos viram `application/json` único com schema INLINE resolvido (mantém
   o formato pinado pelos testes e corrige o drift text/json de atendimento).
3. Um transformer de operação genérico de CSRF: header X-CSRF-TOKEN para qualquer
   action marcada com antiforgery (cookie-only ⇒ required:false; sempre ⇒
   required:true) ou endpoint cookie-only exclusivo (required:true) — via
   interface `IAntiforgeryRequiredMetadata` em Foundation.Identity.
4. Um schema transformer genérico: `additionalProperties: false` em objetos
   quando `UnmappedMemberHandling.Disallow` nas opções de serializer.
5. Program.cs passa a só REGISTRAR: document transformer de security schemes
   (permanece), transformers de correlação/segurança movidos para arquivos
   próprios em `OpenApi/`.
6. Testes que pinam OpenAPI verdes sem edição; `./scripts/verify-public.sh`
   EXIT:0 serializado no head exato; revisão Terra + aprovação Sol via codex CLI;
   evidência no PR (antes/depois de linha e de schema) e comentários em #238/#239.

**Adiamentos:** rotas/status/corpos de erro, camada de dados e segurança não
mudam; McpHost sem surface OpenAPI hoje permanece fora de escopo.
