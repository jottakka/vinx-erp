# ADR 0005: núcleo do agent harness pertence ao módulo AI

## Status

Accepted

## Context

UI, WhatsApp e clientes MCP precisam reutilizar a mesma execução agêntica sem acoplar decisões de modelo aos hosts ou duplicar autorização. A primeira fatia precisa provar o loop entre modelo e tools, mantendo identidade, empresa, capabilities e controles de escrita fora do conteúdo não confiável.

## Decision

O núcleo do agent harness fica no módulo `Vinx.Erp.AI`. AI possui runner, perfis de tarefa, contratos de modelo e de tools, policy de execução e eventos seguros. Foundation continua responsável por identidade, capabilities, auditoria, correlação e idempotência.

Os contratos internos de tools são compatíveis com os conceitos do MCP: nome estável, schemas JSON de entrada e saída, capability exigida, risco e metadados de comportamento. Essa compatibilidade permite adaptar o catálogo para outros clientes sem transformar o harness em host nem expor CRUD ou banco genérico.

O contexto confiável do servidor é a única fonte de ator, empresa e capabilities. Mensagens, contexto externo, OCR, respostas do modelo e argumentos de tool são não confiáveis e não definem escopo. O request do modelo recebe somente a solicitação não confiável e entradas aprovadas; não recebe contexto de execução nem perfil da tarefa.

Esta fatia usa execução em processo e doubles públicos. Ela não adiciona banco, persistência, deleção, migração, provedor real, host, WhatsApp ou servidor MCP remoto. O host MCP remoto, autenticação de clientes, transporte e adaptadores para Claude e GPT serão entregues depois, sobre os mesmos contratos e serviços autorizados.

## Consequences

- Hosts e canais dependem do módulo AI; o módulo AI não depende deles.
- Tools passam por allowlist, empresa, capability e barreira de confirmação antes da validação ou execução.
- Escritas continuam bloqueadas até a integração de confirmação e auditoria prevista em fatias posteriores.
- Provedores e canais podem evoluir sem mover identidade ou autorização para prompts.
- Compatibilidade interna com MCP não significa que um endpoint MCP remoto esteja disponível nesta versão.
