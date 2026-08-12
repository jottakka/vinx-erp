# Identidade e isolamento concorrente do fixture PostgreSQL — issue 179

## Objetivo

Complementar a correção de endpoint da issue #174: cada fixture deve provar
que alcança seu PostgreSQL descartável, preservar a causa primária se o cleanup
falhar e manter isolamento em duas inicializações simultâneas.

## Decisões

- Esta fatia depende da merge de #174 e não altera produção, imagem, pacote,
  paralelismo global, documentos ou frontend.
- A identidade usa banco administrativo exclusivo por fixture e senha efêmera;
  ambos são configurados antes de criar o container. A senha nunca entra em
  record, exceção, log ou documentação.
- A prova semântica consulta `current_database()`, `current_user`,
  `inet_server_port()` e `version()` e compara banco/usuário/porta/versão com a
  identidade esperada.
- Erro primário de criação não pode ser substituído pelo erro de descarte. Se
  ambos falharem, a exceção expõe ambos, mantendo o primário primeiro.
- Retry de readiness externa não entra nesta fatia: o `H` observável ainda não
  tem causa transitória reproduzida. Não haverá `Task.Delay`, polling ou retry
  sem evidência.

## BDD verificável

| Classe | Resultado observável |
| --- | --- |
| Sucesso | A conexão confirma banco e usuário exclusivos do fixture antes do bootstrap. |
| Validação/erros | Um PostgreSQL com banco ou usuário diferente falha com diagnóstico sanitizado. |
| Acesso | N/A — fixture não expõe rota, identidade de usuário ou tenant. |
| Estado/exceções | Falhas primária e de cleanup continuam identificáveis; novo cleanup é possível após falha. |
| Concorrência | Dois fixtures simultâneos confirmam bancos distintos e são descartados sem cruzamento. |

## Limites

As doubles de container, se necessárias para erro de cleanup, ficam em
infraestrutura de testes. O caminho de sucesso e o teste concorrente usam
Testcontainers PostgreSQL real.
