# ADR 0005: núcleo do agent harness pertence ao módulo AI

## Status

Accepted

## Context

UI, WhatsApp e clientes MCP precisam reutilizar a mesma execução agêntica sem acoplar decisões de modelo aos hosts ou duplicar autorização. A primeira fatia precisa provar o loop entre modelo e tools, mantendo identidade, empresa, capabilities e controles de escrita fora do conteúdo não confiável.

## Decision

O núcleo do agent harness fica no módulo `Vinx.Erp.AI`. AI possui runner, contrato e configuração dos perfis de tarefa, contratos neutros de canal e modelo, contratos de tools, policy de execução e eventos seguros. Foundation continua responsável por identidade, empresa, capabilities, auditoria, correlação e idempotência. Uma camada de host/aplicação seleciona um perfil aprovado somente após receber esse contexto confiável de Foundation. Esta decisão substitui a atribuição anterior de perfis ao módulo Foundation no desenho de arquitetura de 2026-08-08.

Os contratos internos de tools são orientados aos conceitos do MCP e preparados para adaptação: nome estável, schemas JSON de entrada e saída, capability exigida, risco e metadados de comportamento. Eles não constituem um contrato MCP externo completo. O host MCP futuro adicionará ator autenticado, `empresa_id` explícito, política de auditoria e correlação, idempotência e ciclo de confirmação definidos no ADR 0003.

O contexto confiável do servidor é a única fonte de ator, empresa e capabilities. Mensagens, contexto externo, OCR, respostas do modelo e argumentos de tool são não confiáveis e não definem escopo. O request do modelo recebe somente a solicitação não confiável e entradas aprovadas; não recebe contexto de execução nem perfil da tarefa.

Esta fatia usa execução em processo e doubles públicos. Ela não adiciona banco, persistência, deleção, migração, provedor real, host, WhatsApp ou servidor MCP remoto. O host MCP remoto, autenticação de clientes, transporte e adaptadores para Claude e GPT serão entregues depois, sobre os mesmos contratos e serviços autorizados.

## Consequences

- Hosts dependem dos contratos neutros e do harness do módulo AI; adaptadores concretos de UI, WhatsApp, HTTP e transporte MCP pertencem aos hosts, e o módulo AI não depende deles.
- Tools passam por allowlist, empresa, capability e barreira de confirmação antes da validação ou execução.
- Escritas continuam bloqueadas até a integração de confirmação e auditoria prevista em fatias posteriores.
- Provedores e canais podem evoluir sem mover identidade ou autorização para prompts.
- Orientação interna a MCP não significa contrato externo completo nem endpoint MCP remoto disponível nesta versão.
