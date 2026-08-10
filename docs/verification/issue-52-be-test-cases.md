# Casos testados — issue 52, ciclo documental

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Dado metadado válido, quando nasce o registro, então fica em `quarantined` com empresa, hash, MIME, tamanho, classificação, origem, retenção e auditoria | `FileRecordLifecycleTests` | Evitar registro sem estado ou escopo | coberto A |
| Dado nome, extensão, MIME, tamanho ou checksum inválido, quando validar upload, então retorna código estável | `DocumentUploadPolicyTests` | Recusar metadado inseguro antes de Storage | coberto A |
| Dado objeto com tamanho, hash ou MIME divergente, ou malware indicado, quando verificar, então falha sem liberar arquivo | `DocumentVerificationPolicyTests` | Bloquear conteúdo alterado ou inseguro | coberto A |
| Dado documento em quarentena, quando verificar, então só transita para `verified` ou `verification_failed` | `FileRecordLifecycleTests` | Impedir transição retroativa ou salto de estado | coberto A |
| Dado exclusão lógica antes da retenção, quando pedir remoção física, então ela é recusada; restauração remove a marca | `FileRecordLifecycleTests` | Preservar retenção e recuperação | coberto A |
| Dado adapter Supabase privado, quando criar URL assinada ou listar/apagar objeto, então usa bucket privado e chaves codificadas | `SupabaseFileStorageTests` | Manter fornecedor atrás de contrato seguro | coberto A |
| Dado escrita concorrente de metadado, quando ocorrer conflito de versão, então EF recusa atualização perdida | `RowVersionTests` | Conservar contrato de concorrência herdado | coberto A |
| Dado ator sem empresa, capability ou tenant correto, quando pedir intenção/confirmação, então recebe negação auditada | PR B da stack #52 | Autorização e auditoria pertencem à intenção/confirmação | adiado B |
| Dado upload válido, quando confirmar duas vezes ou correr em paralelo, então há uma só conclusão idempotente | PR B da stack #52 | Evitar dupla confirmação e exposição em falha | adiado B |
| Dado documento não confirmado, quando pedir acesso, então URL, consumo ou reuso são recusados | PR C da stack #52 | Manter quarentena inacessível | adiado C |
| Dado URL revogada, expirada, repetida ou de outra empresa, quando consumir, então é recusada | PR C da stack #52 | Revalidar acesso temporário e tenant | adiado C |
| Dado objeto órfão ou processamento falho, quando limpar, então só remove após retenção e registra resultado rastreável | PR C da stack #52 | Evitar remoção física prematura | adiado C |
