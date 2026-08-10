# Casos testados — issue 52, ciclo documental

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Dado metadado válido, quando nasce o registro, então fica em `quarantined` com empresa, hash, MIME, tamanho, classificação, origem, retenção e auditoria | `FileRecordLifecycleTests` | Evitar registro sem estado ou escopo | coberto A |
| Dado comando declarativo com nome, MIME, checksum, classificação ou origem inválidos, quando o serviço o valida após autorização, então nega, audita e não cria `FileRecord` nem URL | `DocumentUploadContractValidationTests`, `DocumentUploadEndpointTests` | Preservar códigos estáveis e auditoria da negação; DataAnnotations não antecipam a autorização | coberto B |
| Dado objeto de chave, tamanho, hash ou MIME divergente, quando verificar, então falha sem liberar arquivo | `DocumentVerificationPolicyTests`, `SupabaseFileStorageTests` | Bloquear objeto trocado ou alterado por inspeção server-side | coberto B |
| Dado documento em quarentena, quando verificar, então só transita para `verified` ou `verification_failed` | `FileRecordLifecycleTests` | Impedir transição retroativa ou salto de estado | coberto A |
| Dado exclusão lógica antes da retenção, quando pedir remoção física, então ela é recusada; restauração remove a marca | `FileRecordLifecycleTests` | Preservar retenção e recuperação | coberto A |
| Dado adapter Supabase privado, quando criar URL de upload/download ou listar/apagar objeto, então usa bucket privado, corpo suportado e todas as páginas | `SupabaseFileStorageTests` | Manter fornecedor atrás de contrato seguro sem omitir objetos | coberto A |
| Dado escrita concorrente de metadado, quando ocorrer conflito de versão, então EF recusa atualização perdida | `RowVersionTests` | Conservar contrato de concorrência herdado | coberto A |
| Dado ator sem empresa, capability ou tenant correto, quando pedir intenção/confirmação, então recebe negação auditada | `DocumentUploadEndpointTests` | Autorização e auditoria pertencem à intenção/confirmação | coberto B |
| Dado upload válido, quando confirmar duas vezes ou correr em paralelo, então há uma só conclusão idempotente | `DocumentUploadEndpointTests` | Evitar dupla confirmação e exposição em falha | coberto B |
| Dado scanner antimalware indisponível, malware detectado ou leitura privada falha, quando confirmar, então fica `verification_failed`, com motivo estável e sem URL de download | `DocumentVerificationPolicyTests`, `DocumentUploadEndpointTests`, `SupabaseFileStorageTests` | Falhar fechado sem scanner produtivo | coberto B |
| Dado payload EICAR no double de armazenamento, quando o teste confirma, então o double devolve `MalwareDetected` e a confirmação falha | `DocumentUploadEndpointTests` | Manter EICAR exclusivamente em `tests/public`; produção não inspeciona marcador | coberto B |
| Dado documento não confirmado, quando pedir acesso, então URL, consumo ou reuso são recusados | PR 3 da stack #52 | Manter objeto inacessível enquanto aguarda reconciliação | adiado PR 3 |
| Dado URL revogada, expirada, repetida ou de outra empresa, quando consumir, então é recusada | PR 3 da stack #52 | Revalidar acesso temporário e tenant | adiado PR 3 |
| Dado objeto órfão ou processamento falho, quando limpar ou recuperar, então só atua após retenção e registra resultado rastreável | PR 3 da stack #52 | Evitar remoção física prematura e preservar recuperação | adiado PR 3 |

## Condição de produção

A issue #104 é obrigatória antes de habilitar uploads de produção que exijam
antivírus completo. Sem scanner integrado, `SupabaseFileStorage` devolve
`MalwareScanStatus.Unavailable`; a confirmação falha fechada. EICAR é somente
payload do double em `tests/public`, nunca uma regra de produção.
