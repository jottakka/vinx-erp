# Contrato do módulo de IA

## Ownership

IA possui os contratos do harness supervisionado, o catálogo de ferramentas e
a política determinística de execução. Ela recebe separadamente o contexto
confiável de um host e a entrada não confiável de modelo ou canal. O modelo
recebe somente a requisição, descritores de ferramentas permitidas e resultados
tipados anteriores das ferramentas; ele nunca autoriza uma ação.

A política de ferramentas é avaliada no servidor antes da validação semântica
ou invocação. O harness pode invocar ferramentas somente leitura autorizadas
pelo perfil e pela política recebidos. Todo comando que não seja somente
leitura exige confirmação durável de Foundation antes de um futuro serviço de
aplicação executá-lo. Este módulo não exclui dados de domínio nem possui uma
ferramenta de domínio.

Ele não possui SDKs ou adaptadores de provedor, hosts de transporte, UI,
transporte WhatsApp ou MCP, persistência, processamento OCR ou serviços de
domínio. Essas integrações devem permanecer finas ao redor deste contrato.

## Company scope

Toda ferramenta declarada com `RequiresExplicitCompany` exige uma empresa
explícita no contexto confiável de execução. A integração futura deve fazer
Foundation validar associação, capability e escopo antes de criar o
`AgentExecutionContext` confiável; essa validação ainda não é uma implementação
deste módulo. O modelo nunca infere ou sobrescreve a empresa, e argumentos de
ferramenta não a definem. Foundation permanece responsável por aprovação,
auditoria e idempotência; o harness apenas aplica o perfil recebido e a política
de ferramentas antes da delegação.

## Contract version

`1.0`

As fronteiras de módulos e a separação entre núcleo e hosts seguem a
[arquitetura modular do ERP](../superpowers/specs/2026-08-08-vinx-erp-architecture-design.md).
Nenhum pacote de provedor ou registro de host pertence a este contrato.

## Deletion strategy

Este módulo não possui entidade persistente nem dados de domínio. A política
de exclusão continua sendo do módulo de domínio proprietário e das regras de
auditoria de Foundation.

## Migration notes

Não há schema persistente em #127. Futuras fatias de provedor, transporte,
aprovação, OCR ou ferramenta de domínio exigem suas próprias decisões de
responsabilidade, autorização e migração.
