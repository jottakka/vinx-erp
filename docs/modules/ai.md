# Módulo AI — adiado

## Estado

Não existe runtime AI no código atual. A simplificação removeu o projeto e os
testes que só provavam uma integração não hospedada com o Microsoft Agent
Framework. Nenhum host os consumia, nenhuma API os expunha e nenhuma
funcionalidade do produto dependia deles.

A Foundation mantém apenas a primitiva durável de aprovação
`prepare/confirm/veto`, com autorização, RLS, auditoria e idempotência. Essa
primitiva não implica sessão de agente, execução de ferramenta nem retomada de
workflow.

## Ownership

Enquanto adiado, o módulo não possui código, estado ou host. Quando voltar,
será responsável somente pela orquestração de inferência e ferramentas; regras
de negócio continuam nos módulos e aprovação, identidade, autorização, RLS e
auditoria continuam na Foundation.

## Company scope

Não há requisição AI ativa. A futura fronteira deve receber ator e empresa de
contexto confiável criado pelo servidor, nunca de texto, argumento de tool ou
claim não materializado, e deve reautorizar cada caso de uso na Foundation.

## Ordem de retorno

Código AI volta somente em uma fatia vertical com consumidor de produção:

1. #110 entrega bootstrap HTTP para o frontend;
2. #132 entrega contexto confiável neutro de transporte;
3. #135 integra provider real por SDK oficial e `IChatClient`;
4. #136 valida e redige a saída real;
5. #138 conecta contexto, provider, guardrails e aprovação numa API
   supervisionada.

Quando essa fatia começar, a primeira opção continua sendo Microsoft Agent
Framework e SDK oficial do provider. Não serão recriados loop de chat,
dispatcher de ferramentas, timers, fila, checkpoint ou workflow próprios sem
caso de uso hospedado e evidência de que a plataforma não atende.

## Contract version

`2.0`

## Deletion strategy

O módulo não mantém dados para excluir. Aprovações duráveis continuam sob a
retenção, soft-delete e futura purga física definidas pela Foundation.

## Migration notes

O módulo não possui migrações. As tabelas, policies, grants e capabilities de
aprovação permanecem migrações da Foundation e não serão duplicadas quando o
runtime AI retornar.
