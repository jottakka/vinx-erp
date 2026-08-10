# Matriz pública — admissão interna de sessões

Cobertura da microfatia interna da issue #21. Gate HTTP, endpoint e revogação
global própria permanecem em PR posterior.

| Classe | Cobertura pública desta PR |
|---|---|
| Sucesso | Prova nova cria uma sessão e uma auditoria global; repetição fica ativa sem novo evento. |
| Validação e erros | Barreira estrita em `-1`, `0` e `+1` segundo; sessão revogada; usuário interno ausente e cancelamento. |
| Acesso | Função de lock e grants mínimos são cobertos por `SessionMigrationTests`; HTTP é N/A nesta microfatia. |
| Estado e exceções | Falha de auditoria em sucesso ou negação retorna indisponibilidade e não deixa sessão nem evento parcial. |
| Concorrência | Duas admissões do mesmo usuário deixam uma sessão e uma auditoria; colisão entre usuários é negada sem recurso. |
| Observabilidade | Sucesso em `Information`, negação em `Debug`, colisão em `Warning`, falha em `Error` com exceção, propriedades seguras e correlação apenas no scope. |
