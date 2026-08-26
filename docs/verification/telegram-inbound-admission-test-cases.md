# Casos BDD — admissão de texto Telegram

## Escopo

Esqueleto da Task 3.1. A matriz direta de PostgreSQL e seus nomes de testes
serão finalizados na Task 3.2; este corte não cria endpoint, publicação ou
worker.

| Classe BDD | Dado / quando / então | Evidência automatizada |
| --- | --- | --- |
| Sucesso | Dado um contato Telegram privado validado e uma única membership ativa, quando resolver e admitir texto, então uma linha `pending` guarda a identidade resolvida e retorna `created`. | Pendente — Task 3.2 |
| Validação e erros | Dado IDs inválidos, texto vazio ou maior que 4096, ou tupla de resolução inválida, quando a função é chamada, então retorna SQLSTATE `22023`. | Pendente — Task 3.2 |
| Acesso | Dado contato ausente, não validado, inativo ou com mais de uma identidade ativa, quando o resolver é chamado, então retorna estado fechado sem IDs; runtime executa somente as funções estreitas. | Pendente — Task 3.2 |
| Estado e exceções | Dado replay idêntico ou reutilização incompatível de `(chat_id, message_id)`, quando a admissão ocorre, então retorna `replay` ou `conflict` sem alterar a primeira evidência. | Pendente — Task 3.2 |
| Concorrência | Dadas admissões simultâneas do mesmo chat e mensagem, quando as transações concluem, então uma cria a linha e a outra classifica sem expor violação UNIQUE. | Pendente — Task 3.2 |
