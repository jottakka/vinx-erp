# Casos testados — issue 52, ciclo documental

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Dado metadado válido, quando nasce o registro, então fica em `quarantined` com empresa, hash, MIME, tamanho, classificação, origem, retenção e auditoria | `FileRecordLifecycleTests` | Evitar registro sem estado ou escopo | coberto A |
| Dado nome, extensão, MIME, tamanho ou checksum inválido ou fora do limite EF, quando validar upload, então retorna código estável | `DocumentUploadPolicyTests` | Recusar metadado inseguro antes de Storage/persistência | coberto A |
| Dado objeto de chave, tamanho, hash ou MIME divergente, ou marcador EICAR, quando verificar, então falha sem liberar arquivo | `DocumentVerificationPolicyTests`, `SupabaseFileStorageTests` | Bloquear objeto trocado, alterado ou inseguro por inspeção server-side | coberto B |
| Dado documento em quarentena, quando verificar, então só transita para `verified` ou `verification_failed` | `FileRecordLifecycleTests` | Impedir transição retroativa ou salto de estado | coberto A |
| Dado exclusão lógica antes da retenção, quando pedir remoção física, então ela é recusada; restauração remove a marca | `FileRecordLifecycleTests` | Preservar retenção e recuperação | coberto A |
| Dado adapter Supabase privado, quando criar URL de upload/download ou listar/apagar objeto, então usa bucket privado, corpo suportado e todas as páginas | `SupabaseFileStorageTests` | Manter fornecedor atrás de contrato seguro sem omitir objetos | coberto A |
| Dado escrita concorrente de metadado, quando ocorrer conflito de versão, então EF recusa atualização perdida | `RowVersionTests` | Conservar contrato de concorrência herdado | coberto A |
| Dado ator sem empresa, capability ou tenant correto, quando pedir intenção/confirmação, então recebe negação auditada | `DocumentUploadEndpointTests` | Autorização e auditoria pertencem à intenção/confirmação | coberto B |
| Dado upload válido, quando confirmar duas vezes ou correr em paralelo, então há uma só conclusão idempotente | `DocumentUploadEndpointTests` | Evitar dupla confirmação e exposição em falha | coberto B |
| Dado leitura privada falha ou marcador EICAR é encontrado, quando confirmar, então fica `verification_failed` e não há rota de download | `DocumentUploadEndpointTests` | Manter conteúdo inacessível após falha de inspeção | coberto B |
| Dado documento não confirmado, quando pedir acesso, então URL, consumo ou reuso são recusados | PR C da stack #52 | Manter quarentena inacessível | adiado C |
| Dado URL revogada, expirada, repetida ou de outra empresa, quando consumir, então é recusada | PR C da stack #52 | Revalidar acesso temporário e tenant | adiado C |
| Dado objeto órfão ou processamento falho, quando limpar, então só remove após retenção e registra resultado rastreável | PR C da stack #52 | Evitar remoção física prematura | adiado C |

## Condição de produção

A issue #104 é obrigatória antes de habilitar uploads de produção que exijam
antivírus completo. Esta fatia apenas detecta o marcador EICAR durante a
inspeção server-side dos bytes no `FileStorage`; não substitui provider de AV.
