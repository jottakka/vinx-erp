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
  `inet_server_port()` e `version()` e compara banco/usuário/porta com a
  identidade esperada; a versão confirma somente a família PostgreSQL.
- Erro primário de criação não pode ser substituído pelo erro de descarte. Se
  ambos falharem, a exceção expõe ambos, mantendo o primário primeiro. A factory
  aplica a mesma regra ao bootstrap, migração e seed, ainda tentando descartar
  host, JWT e fixture.
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

Uma seam interna, usada apenas pelo projeto de testes, abstrai start, endpoint e
dispose do container. A construção normal continua com `PostgreSqlBuilder`; a
double exercita a falha de cleanup. O caminho de sucesso e o teste concorrente
usam Testcontainers PostgreSQL real. Uma entrada interna da factory recebe uma
fixture controlada apenas nos testes de falha de migração/seed e cleanup.
