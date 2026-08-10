# Correção de upload documental — issue 52 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Reentregar o upload documental revisado na main, mantendo-o em quarentena segura quando não há scanner antimalware de produção.

**Architecture:** A branch nova reaplica primeiro, sem alteração de árvore, os quatro commits da PR #107. As correções seguintes são commits separados: o comando usa DataAnnotations manualmente depois da autorização/auditoria, e a inspeção do Supabase comunica veredito antimalware indisponível em vez de procurar EICAR. O armazenamento continua privado e a limpeza física fica na PR 3.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, EF Core/Npgsql, xUnit, Testcontainers e Supabase Storage por HttpClient.

## Global Constraints

- Nunca ler, listar ou executar quality/private.
- Não tocar Paguai, vínculos da #39, UI, MCP, e-mail, WhatsApp, Kanban, evento novo ou provider de storage/scanner novo.
- Preservar FileRecord, FileStorage e Supabase Storage; sem migration adicional além da migration já revisada da PR #107.
- Testes e configuração não contam na estimativa do responsável; arquivos não-teste estimados: 14. Linhas manuais estimadas: aproximadamente 1.250; migration designer gerado: 1.250 linhas separadas.
- Cada patch de upload reaplicado deve ter o mesmo patch-id de seu commit em e89a9d8 antes dos commits corretivos. Arquivos com hunks pré-existentes de logging/sessão na main preservam esses hunks; toda divergência de patch de upload pertence exclusivamente aos commits corretivos desta especificação.
- Validação rejeitada deve continuar auditada. Não chamar AddValidation() nem deixar Minimal API rejeitar o corpo antes de DocumentUploadService.
- Sem scanner real, confirmar falha fechada com malware_scan_unavailable; nenhum documento novo é confirmado ou recebe URL de download em produção até a #104.

---

### Task 1: Reaplicar a PR #107 sem alterar código revisado

**Files:**
- Create: arquivos já presentes em c8aa401..e89a9d8, incluindo DocumentUploadService.cs, DocumentUploadContracts.cs, migration 20260810022253_GrantRuntimeFileMutations.cs e seus testes públicos.
- Modify: docs/verification/issue-52-be-test-cases.md, FileRecord.cs, DocumentUploadPolicy.cs, DocumentVerificationPolicy.cs, FileStorage.cs, SupabaseFileStorage.cs e Program.cs, exatamente como em e89a9d8.

**Interfaces:**
- Consumes: FileRecord, FileStorage, AuditAppender, ActorContext, FoundationDbContext e capabilities herdadas.
- Produces: CreateDocumentUploadIntentCommand, ConfirmDocumentUploadCommand, DocumentUploadService e endpoints de intenção/confirmação, inicialmente equivalentes à PR #107.

- [x] **Step 1: Confirmar base limpa e ausência de upload em main**

Run:

~~~bash
git status -sb
git merge-base --is-ancestor e89a9d8 HEAD; test $? -eq 1
git ls-tree -r --name-only HEAD -- src/Foundation/Vinx.Erp.Foundation/Files | grep -q 'DocumentUploadService.cs'; test $? -eq 1
~~~

Expected: working tree contém apenas plano/especificação; e89a9d8 e DocumentUploadService.cs não estão na base.

- [x] **Step 2: Reaplicar quatro commits revisados sem editar arquivos**

Run:

~~~bash
git cherry-pick -x a22eec46c739d1c5f4e5e27bcbfdd2e0586e856d
git cherry-pick -x dbcc710bea97105c59925c03be910e03950b9342
git cherry-pick -x ff9dc691e6b69ef5a6cb74e45085bd24f2b4e8e6
git cherry-pick -x e89a9d89357189f9034d77c0bff137d0805bbaa9
~~~

Expected: quatro commits novos, cada um com trailer cherry picked from; nenhum conflito, pois atualização de main é fora do Foundation de documentos.

- [x] **Step 3: Verificar equivalência exata antes das correções**

Run:

~~~bash
for pair in \
  'a22eec46c739d1c5f4e5e27bcbfdd2e0586e856d HEAD~3' \
  'dbcc710bea97105c59925c03be910e03950b9342 HEAD~2' \
  'ff9dc691e6b69ef5a6cb74e45085bd24f2b4e8e6 HEAD~1' \
  'e89a9d89357189f9034d77c0bff137d0805bbaa9 HEAD'; do
  set -- $pair
  test "$(git show --format= "$1" | git patch-id --stable | cut -d' ' -f1)" = "$(git show --format= "$2" | git patch-id --stable | cut -d' ' -f1)"
done
git range-diff c8aa401...e89a9d8 HEAD~4...HEAD
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentUploadPolicyTests|FullyQualifiedName~DocumentVerificationPolicyTests|FullyQualifiedName~FileRecordLifecycleTests|FullyQualifiedName~SupabaseFileStorageTests'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter 'FullyQualifiedName~DocumentUploadEndpointTests'
~~~

Expected: quatro patch-ids iguais, range-diff sem mudança de patch e testes existentes verdes. As diferenças integrais esperadas em Program.cs e FoundationSchemaTests.cs são hunks pré-existentes da main de logging/sessão; nunca removê-los para igualar blobs.

### Task 2: Validar contrato com DataAnnotations sem perder auditoria

**Files:**
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadContracts.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadPolicy.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs
- Create: tests/public/Vinx.Erp.Foundation.UnitTests/DocumentUploadContractValidationTests.cs
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs

**Interfaces:**
- Consumes: CreateDocumentUploadIntentCommand e constantes de DocumentUploadPolicy.
- Produces: DocumentUploadIntentCommandValidation.Validate(CreateDocumentUploadIntentCommand): string?, retornando os mesmos códigos estáveis usados por DocumentUploadService.

- [x] **Step 1: Escrever teste RED para contrato declarativo**

Criar teste unitário com um caso por campo: nome vazio e acima de MaxFileNameLength, MIME vazio/acima de MaxMimeTypeLength, checksum vazio/acima de MaxChecksumLength, classificação e origem vazias/acima de seus limites. Cada caso chama DocumentUploadIntentCommandValidation.Validate(ValidIntent() with { ... }) e espera o código existente, por exemplo file_name_required, file_name_too_long, mime_required, checksum_invalid, classification_required e origin_too_long.

- [x] **Step 2: Executar RED**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentUploadContractValidationTests'
~~~

Expected: FAIL porque DocumentUploadIntentCommandValidation não existe.

- [x] **Step 3: Implementar anotações e validação manual mínima**

Em CreateDocumentUploadIntentCommand, importar System.ComponentModel.DataAnnotations e aplicar property Required e StringLength apenas aos campos cujas mensagens equivalem aos códigos existentes. Usar mensagens exatas: file_name_required, file_name_too_long, mime_required, mime_type_too_long, checksum_invalid, checksum_too_long, classification_required, classification_too_long, origin_required e origin_too_long.

Adicionar no mesmo arquivo:

~~~csharp
public static class DocumentUploadIntentCommandValidation
{
    public static string? Validate(CreateDocumentUploadIntentCommand command)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(command, new ValidationContext(command), results, true)
            ? DocumentUploadPolicy.Validate(command.FileName, command.MimeType, command.SizeBytes, command.Checksum)
                ?? DocumentUploadPolicy.ValidateIntentMetadata(
                    command.Classification, command.Origin, command.RetentionUntil)
            : results[0].ErrorMessage;
    }
}
~~~

Extrair MaxClassificationLength e MaxOriginLength como constantes de DocumentUploadPolicy, sem mudar limite. Em CreateIntentAsync, substituir o bloco atual por chamada a DocumentUploadIntentCommandValidation.Validate(command), mantendo ReturnAuditedDenialAsync e HTTP 400.

- [x] **Step 4: Verificar GREEN e auditoria de endpoint**

Adicionar em DocumentUploadEndpointTests dois casos: nome ausente e nome acima do limite. Ambos confirmam HTTP 400/código estável, nenhum FileRecord e um AuditOutcome.Denied com o mesmo motivo.

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentUploadContractValidationTests|FullyQualifiedName~DocumentUploadPolicyTests'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter 'FullyQualifiedName~DocumentUploadEndpointTests'
~~~

Expected: PASS. Program.cs não ganha AddValidation().

- [x] **Step 5: Commit**

~~~bash
git add src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadContracts.cs src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadPolicy.cs src/Foundation/Vinx.Erp.Foundation/Files/DocumentUploadService.cs tests/public/Vinx.Erp.Foundation.UnitTests/DocumentUploadContractValidationTests.cs tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs
git commit -m "fix(foundation): audit document upload validation"
~~~

### Task 3: Remover EICAR produtivo e falhar fechado sem scanner

**Files:**
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/FileStorage.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/DocumentVerificationPolicy.cs
- Modify: src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs
- Modify: tests/public/Vinx.Erp.Foundation.UnitTests/DocumentVerificationPolicyTests.cs
- Modify: tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseFileStorageTests.cs
- Modify: tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs

**Interfaces:**
- Consumes: bytes privados lidos por SupabaseFileStorage e StorageObjectInspection.
- Produces: MalwareScanStatus com Clean, MalwareDetected e Unavailable; DocumentVerificationPolicy.Validate retorna malware_scan_unavailable para Unavailable.

- [x] **Step 1: Escrever teste RED para status de scanner ausente**

Adicionar em DocumentVerificationPolicyTests:

~~~csharp
[Fact]
public void Inspection_contract_exposes_an_antimalware_status()
{
    var property = typeof(StorageObjectInspection).GetProperty("MalwareScanStatus");

    Assert.NotNull(property);
    Assert.Equal("MalwareScanStatus", property!.PropertyType.Name);
}
~~~

Adicionar caso que constrói inspeção com Unavailable e espera malware_scan_unavailable.

- [x] **Step 2: Executar RED**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentVerificationPolicyTests'
~~~

Expected: primeiro teste falha pela propriedade ausente. Depois da introdução do tipo para torná-lo compilável, caso de política falha até regra fail-closed existir.

- [x] **Step 3: Introduzir status explícito e regra fail-closed**

Substituir booleano de StorageObjectInspection por:

~~~csharp
public enum MalwareScanStatus
{
    Clean,
    MalwareDetected,
    Unavailable
}

public sealed record StorageObjectInspection(
    StorageObjectMetadata Metadata,
    MalwareScanStatus MalwareScanStatus);
~~~

Em DocumentVerificationPolicy.Validate, retornar malware_scan_unavailable para Unavailable e malware_detected para MalwareDetected antes de liberar confirmação. Preservar chave, tamanho, checksum e MIME.

Em SupabaseFileStorage, manter leitura limitada, cálculo SHA-256 e limpeza de buffer. Remover EicarMarker, tabela KMP, índice e métodos ContainsEicarMarker/CreateFailureTable; retornar Unavailable.

- [x] **Step 4: Mover EICAR para testes e verificar GREEN**

No storage falso em DocumentUploadEndpointTests, detectar EicarPayload exclusivamente no teste e devolver MalwareDetected; devolver Clean para conteúdo normal. Atualizar teste unitário do adapter para esperar Unavailable. Adicionar cenário de endpoint com Unavailable, esperando verification_failed, malware_scan_unavailable, auditoria de falha e ausência de URL de download.

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentVerificationPolicyTests|FullyQualifiedName~SupabaseFileStorageTests'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter 'FullyQualifiedName~DocumentUploadEndpointTests'
~~~

Expected: PASS. Executar rg -n 'EicarMarker|ContainsEicarMarker|CreateFailureTable' src e esperar código 1; EICAR permanece somente em tests/public.

- [x] **Step 5: Commit**

~~~bash
git add src/Foundation/Vinx.Erp.Foundation/Files/FileStorage.cs src/Foundation/Vinx.Erp.Foundation/Files/DocumentVerificationPolicy.cs src/Foundation/Vinx.Erp.Foundation/Files/SupabaseFileStorage.cs tests/public/Vinx.Erp.Foundation.UnitTests/DocumentVerificationPolicyTests.cs tests/public/Vinx.Erp.Foundation.UnitTests/SupabaseFileStorageTests.cs tests/public/Vinx.Erp.Foundation.IntegrationTests/DocumentUploadEndpointTests.cs
git commit -m "fix(foundation): fail closed document scanning"
~~~

### Task 4: Documentar fronteiras, verificar e preparar entrega ao controlador

**Files:**
- Modify: docs/verification/issue-52-be-test-cases.md
- Modify: docs/superpowers/specs/2026-08-10-issue-52-upload-correction-design.md
- Modify: docs/superpowers/plans/2026-08-10-issue-52-upload-correction.md

**Interfaces:**
- Consumes: resultados das Tasks 1–3 e #104 como dependência externa.
- Produces: manifesto BDD curto, evidência de equivalência e material para draft PR do controlador.

- [x] **Step 1: Atualizar manifesto BDD**

Trocar referência de EICAR produtivo por três cenários: comando declarativo negado/auditado; veredito Unavailable falha fechado; EICAR é payload exclusivo do double de teste. Manter URL, revogação, cleanup e retenção adiados para PR 3, citando que objeto permanece inacessível enquanto aguarda reconciliação.

- [x] **Step 2: Validar documentação, identidade e escopo**

Run:

~~~bash
git diff --check origin/main...HEAD
git log --oneline origin/main..HEAD
git diff --stat origin/main...HEAD
git diff --name-only origin/main...HEAD | grep -v '^tests/' | wc -l
rg -n 'EicarMarker|ContainsEicarMarker|CreateFailureTable' src; test $? -eq 1
rg -n 'EICAR' tests/public
~~~

Expected: sem whitespace; somente arquivos estimados e testes; marcador ausente de src e presente apenas em testes.

- [x] **Step 3: Rodar testes públicos focados e gate completo**

Run:

~~~bash
dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter 'FullyQualifiedName~DocumentUpload|FullyQualifiedName~DocumentVerification|FullyQualifiedName~SupabaseFileStorage|FullyQualifiedName~FileRecordLifecycle'
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --filter 'FullyQualifiedName~DocumentUploadEndpointTests'
./scripts/verify-public.sh
~~~

Expected: todos verdes. Não declarar sucesso sem código de saída explícito e contagens de testes registradas na PR.

- [ ] **Step 4: Revisões externas e publicação pelo controlador**

Pedir revisão independente do diff e revisão de segurança focada em persistência antes da URL, auditoria após DataAnnotations manual, Unavailable fail-closed, ausência de EICAR produtivo, isolamento de empresa e idempotência. Corrigir cada achado com novo RED/GREEN.

~~~bash
git add docs/verification/issue-52-be-test-cases.md docs/superpowers/specs/2026-08-10-issue-52-upload-correction-design.md docs/superpowers/plans/2026-08-10-issue-52-upload-correction.md
git commit -m "docs: verify document upload correction"
~~~

Não fazer push nem abrir PR nesta Task 4. O controlador, após revisão
independente e revisão de segurança, abrirá draft PR para `main` com causa da
entrega anterior não ter alcançado `main`; equivalência comprovada da base
revisada; correções separadas; estimativa/linhas geradas; BDD; gates; #104 e
limite da PR 3.
