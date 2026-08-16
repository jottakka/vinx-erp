# Casos de verificação — operador de plataforma

Esta matriz preserva as garantias duráveis do bootstrap global da Foundation.
Os testes públicos automatizados são a evidência executável; este documento
explica as fronteiras que não podem regredir.

| Área | Cenário | Resultado exigido |
|---|---|---|
| Roles e RLS | Runtime ou PUBLIC tenta consultar ou alterar o registro global. | Acesso negado; somente o owner dedicado mantém as colunas necessárias. |
| Roles e RLS | Runtime executa a consulta do ator atual. | A função só considera `app.external_identity_id` resolvido pelo servidor na transação; ID ausente, ID do cliente ou outra identidade do mesmo `User` não autorizam. |
| Bootstrap | Primeiro subject verificado é executado duas vezes. | Uma identidade, um registro singleton e uma auditoria de sucesso; a segunda execução devolve `already_configured`. |
| Bootstrap | Outro subject, inclusive ligado ao mesmo `User`, tenta bootstrap depois do singleton. | Conflito auditado, sem criar ou anexar identidade parcialmente. |
| Bootstrap | Duas execuções concorrentes usam subject igual ou diferente. | A constraint singleton mantém um vencedor determinístico e não duplica auditoria de sucesso. |
| Provedor | Admin Auth devolve subject, audiência, confirmação, identidade ou estado inválido. | O comando devolve categoria segura e não chama a função SQL. |
| API | Ator sem registro global acessa a policy. | `403 platform_operator_required` e auditoria global de negação, sem dados de empresa. |
| API | Outra identidade externa vinculada ao mesmo `User` do operador acessa a policy. | `403 platform_operator_required`; somente a identidade fixada no singleton é aceita. |
| API | Consulta do registro ou auditoria está indisponível. | `503 platform_operator_unavailable`; a resposta não é parcialmente autorizada. |
| API | Registro global existe para o ator. | `GET /foundation/bootstrap` projeta a flag e a rota protegida devolve somente `isPlatformOperator: true`. |
| Hardening SQL | Runtime cria relações temporárias com nomes das tabelas de segurança ou auditoria. | As funções usam relações qualificadas e `pg_temp` por último; a decisão e a auditoria durável não são desviadas. |

O comando de deployment usa o endpoint Admin `getUserById` apenas com
credencial de servidor. A decisão de adoção do SDK oficial C# permanece
adiada: o cliente C# documentado pelo provedor é comunitário, portanto esta
fatia usa uma chamada HTTP tipada, pequena e testada.
