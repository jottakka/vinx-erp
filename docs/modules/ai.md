# Contrato do módulo AI

O módulo AI contém a execução agêntica supervisionada. Ele não é uma fronteira de identidade, autorização, auditoria ou transporte.

## Ownership

AI é responsável pelos contratos neutros de canal, comportamento do agent harness, contrato e configuração de perfis de tarefa, adaptadores de modelos e eventos seguros de execução. Hosts são responsáveis pelos adaptadores concretos de UI, WhatsApp, HTTP e transporte MCP.

Foundation é responsável por identidade do ator, empresa, capabilities efetivas, auditoria, correlação e idempotência. Depois desse contexto confiável ser fornecido, uma camada de host/aplicação seleciona um perfil aprovado. O módulo AI não recria contexto nem perfil a partir de texto, arquivos ou respostas do modelo.

## Company scope

A empresa ativa existe somente no contexto confiável criado pelo servidor. Mensagem do usuário, contexto externo, OCR, argumentos de tool e saída do modelo jamais definem ou substituem esse escopo. Campos de empresa recebidos em argumentos não chegam à tool; a execução usa exclusivamente a empresa do contexto confiável.

O request enviado ao modelo contém somente a solicitação não confiável e entradas aprovadas para o modelo. Ele não recebe o contexto de execução confiável nem o perfil da tarefa. Toda tool é resolvida e avaliada contra allowlist, empresa, capability e risco antes de validar ou executar argumentos.

## Contract version

`1.0`

## Limites da V1

A primeira fatia contém contratos, policy e runner em processo, com testes públicos. Ela não contém banco ou persistência de AI, deleção, migração, SDK ou adaptador de provedor real, host, WhatsApp ou MCP remoto.

Os contratos internos de tools são orientados a MCP e preparados para adaptação, mas não formam um contrato MCP externo completo. O host MCP futuro adicionará ator autenticado, `empresa_id` explícito, auditoria e correlação, idempotência e ciclo de confirmação conforme o ADR 0003.

## Deletion strategy

Não há dados persistidos pelo módulo AI nesta versão; portanto, não existe workflow de deleção. Uma futura persistência exigirá estratégia explícita, auditada e isolada por empresa antes de ser adicionada.

## Migration notes

Esta versão não cria schema, tabela, migração ou snapshot. Mudanças futuras de persistência pertencem a uma fatia própria e devem preservar o escopo confiável de empresa.

## Observabilidade

Eventos do harness expõem apenas metadados seguros e de cardinalidade controlada. Prompts, mensagens, conteúdo de OCR, argumentos, resultados de tools, identificadores fornecidos pelo modelo e conteúdo de arquivos não entram em logs nem eventos operacionais.
