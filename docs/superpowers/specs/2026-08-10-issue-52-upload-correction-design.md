# Correção de entrega do upload documental — issue 52

## Objetivo

Reentregar em uma nova PR para `main` a implementação revisada da PR #107,
que foi mesclada na base empilhada depois de a PR #103 já ter sido mesclada.
Corrigir a validação de entrada e o uso indevido do marcador EICAR sem mudar o
modelo `FileRecord` nem o provedor Supabase Storage.

## Limites

- A nova branch parte de `main` e reaplica, sem alteração, os commits
  `a22eec4`, `dbcc710`, `ff9dc69` e `e89a9d8` antes das correções.
- A equivalência da base reaplicada será comprovada por `git range-diff` e
  `git patch-id --stable` de cada commit. Arquivos também alterados por mudanças
  pré-existentes de logging/sessão na `main` preservam esses hunks; identidade
  de patch, e não blob integral, prova a reentrega do código revisado.
- `DataAnnotations` validam somente limites estruturais do comando de
  intenção. A validação é invocada dentro de `DocumentUploadService`, depois
  de empresa, membership e capability, para manter o erro negado auditável.
  `DocumentUploadPolicy` continua canônica para checksum, MIME/extensão,
  retenção e demais regras de negócio.
- O marcador EICAR sai de `SupabaseFileStorage` e fica apenas nos doubles de
  teste. O adapter devolve veredito antimalware indisponível; confirmação falha
  fechada e o objeto permanece inacessível em quarentena até a integração de
  scanner real da #104.
- A intenção persiste `FileRecord` antes de emitir URL de upload. Portanto,
  erro posterior não produz objeto sem metadado; um objeto enviado mas não
  confirmado continua inacessível e rastreável. Reconciliação, limpeza e
  recuperação física continuam exclusivamente na PR 3.
- Não adicionar Paguai, vínculos da #39, endpoint de download, scanner/provedor
  novo, migração adicional, UI ou evento novo.

## Fluxo

1. Usuário autorizado cria intenção com metadado estrutural e documental
   válido; o serviço grava o registro em quarentena e só então emite a URL
   privada de upload.
2. Confirmação lê objeto no servidor, recalcula hash/tamanho/MIME e exige um
   veredito antimalware limpo.
3. Sem scanner de produção, o veredito é indisponível: documento fica em
   `verification_failed`, sem URL de download. Em testes, um double pode
   devolver `clean` ou `malware_detected`; EICAR é apenas payload de teste.
4. Confirmação repetida preserva resultado idempotente; corrida usa lock
   transacional já revisado.

## BDD

| Classe | Dado / Quando / Então |
|---|---|
| Sucesso | Dado veredito limpo de um double de scanner, quando usuário autorizado confirma objeto íntegro, então há um único documento confirmado e auditado. |
| Validação/erros | Dado comando declarativo inválido, quando o serviço o valida depois de autorização e auditoria, então recebe código estável, negação auditada, sem `FileRecord` e sem URL. |
| Acesso | Dado ausência de membership, capability ou empresa correta, quando cria intenção ou confirma, então operação é negada sem expor metadado de outro tenant. |
| Estado/exceções | Dado `MalwareScanStatus.Unavailable`, malware ou falha de leitura, quando confirma, então documento fica em `verification_failed`, com motivo estável, e objeto não fica acessível. |
| Double de teste | Dado payload EICAR no double de armazenamento, quando confirma, então o double devolve `MalwareDetected`; produção não procura EICAR. |
| Concorrência | Dada confirmação repetida com mesmo `request_id`, quando concorrentes confirmam, então uma conclusão persiste e repetições retornam resultado idempotente. |

## Estimativa e entrega

- 14 arquivos não-teste: 11 da PR #107, esta especificação, o plano e o
  manifesto de verificação atualizado. Testes e configuração ficam fora da
  contagem por decisão do responsável.
- Aproximadamente 1.250 linhas manuais, incluindo código e documentação. O
  alvo de 800 linhas é excedido de modo justificado:
  uma única fatia de segurança reentrega o upload já revisado e evita uma
  transição insegura entre PRs.
- Uma migration e seu designer são reaplicados; as 1.250 linhas geradas do
  designer são reportadas à parte.
- Antes de publicação, o controlador executará revisão independente e revisão
  adicional de segurança, testes públicos focados e uma execução do gate
  público completo. Esta Task 4 não faz push nem abre PR; entrega evidências e
  documentação para essas revisões externas.
