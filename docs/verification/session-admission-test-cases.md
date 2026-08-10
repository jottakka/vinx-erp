# Matriz pública — admissão interna de sessões

Esta matriz cobre a primeira PR parcial de #21. O gate HTTP e `revoke-all`
ficam na PR empilhada seguinte; por isso acesso HTTP é N/A nesta entrega.

| Classe BDD | Intenção | Testes públicos | Situação |
|---|---|---|---|
| Sucesso | Nova prova cria uma sessão e auditoria; repetição fica ativa. | `InternalSessionAdmissionServiceTests` | Planejado nesta PR. |
| Validação e erros | Datas inválidas, barreira anterior/igual e vínculo incompatível são negados. | `UserSessionTests`, `InternalSessionAdmissionServiceTests` | Planejado nesta PR. |
| Acesso ao banco | Runtime possui somente `SELECT`/`INSERT` em `user_sessions`, executa a função de lock e não lê `users`. | `FoundationSchemaTests`, `SessionMigrationTests` | Planejado nesta PR. |
| Acesso HTTP | Gate comum bloqueia antes do handler. | — | N/A nesta microfatia: será coberto na próxima PR de #21. |
| Estado e exceções | Sessão revogada é negada; perda de auditoria reverte tudo; erro técnico preserva exceção apenas no log. | `InternalSessionAdmissionServiceTests` | Planejado nesta PR. |
| Concorrência | Duas admissões convergem em uma sessão; colisão entre usuários é segura; downgrade não perde uma mutação concorrente. | `InternalSessionAdmissionServiceTests`, `SessionMigrationTests` | Planejado nesta PR. |
