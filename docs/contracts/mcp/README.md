# Política de contratos MCP

MCP é um transporte de primeira classe. Uma ferramenta de módulo chama o
mesmo serviço de aplicação usado pela API HTTP e preserva os mesmos contratos
de empresa, autorização, negócio, auditoria e observabilidade.

Cada manifesto documenta intenção útil, módulo responsável, JSON Schema de
entrada e saída, `empresa_id` explícita, capability e escopo, risco,
correlação, auditoria, idempotência de escrita e prepare/confirm quando
aplicável. Discovery é conveniência; a invocação sempre reautoriza ator e
empresa.

Nenhuma ferramenta pode oferecer CRUD ou SQL genérico, inferir empresa para um
comando, expor credencial de provider ou contornar o caso de uso do módulo.
Leitura, resumo e rascunho são os primeiros resultados voltados ao agente.
Ações financeiras, externas ou irreversíveis exigem confirmação humana ligada
a ator, empresa, ação e hash do payload.

A baseline da #207 expõe somente `/mcp` stateless pelo SDK oficial e a sonda
constante `vinx_ping`. Ela exige JWT Supabase pré-obtido, limita requisições,
restringe `Host` e rejeita todo `Origin`; não anuncia OAuth MCP, não cria
`ActorContext` e não autoriza ferramenta de domínio. #133, #132 e #125 devem
fechar essas fronteiras antes de qualquer acesso a dados.

O checklist canônico está em [MCP tool checklist](../../modules/mcp-tool-checklist.md).
