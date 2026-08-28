# Revisão de simplicidade e limpeza da main

## Objetivo

Revisar o código público atual a partir da `main` atualizada, procurando
duplicação real, abstrações sem consumidor, código morto, complexidade acidental,
documentação redundante ou desatualizada e sinais de overengineering. A entrega
será uma única PR com apenas correções justificadas e verificáveis.

## Escopo e limites

- Revisar `src/`, `apps/web/`, testes públicos, scripts, infraestrutura e
  documentação versionada que descreve esses artefatos.
- Não ler, listar nem executar `quality/private`.
- Não adicionar funcionalidade, dependência, framework, camada de persistência,
  broker, cache ou abstração sem consumidor atual.
- Preservar contratos de autorização, empresa, auditoria, idempotência,
  concorrência, entrega externa, MCP, OpenAPI e segurança.
- Excluir do cleanup mecânico snapshots, migrations e lockfiles gerados, salvo
  quando um finding demonstrar drift ou código duplicado executável.
- Uma correção entra na PR somente se tiver causa observável, impacto claro,
  escopo pequeno e prova focada. Nits de estilo e preferências sem retorno são
  registrados como adiados, não viram churn.

## Critérios de finding

1. **KISS/YAGNI:** remover caminho morto ou opção não consumida; reduzir
   ramificação, estado ou indireção sem alterar o contrato.
2. **DRY:** consolidar somente lógica semanticamente igual, com consumidores
   atuais e testes que preservem diferenças intencionais.
3. **SOLID:** separar responsabilidade somente quando a classe/módulo mistura
   contratos independentes ou a mudança reduz acoplamento comprovadamente.
4. **Documentação:** remover cópia histórica ou texto contraditório; manter
   decisões, contratos, runbooks e instruções necessários para operação e
   verificação.
5. **Overengineering:** rejeitar generalização especulativa, configurabilidade
   sem uso, wrappers que apenas repassam chamadas e testes que só repetem a
   implementação.

## Execução

Cinco revisões Luna independentes cobrem Foundation/identidade/banco,
AI/documentos, módulos/hosts, frontend e infraestrutura/testes/documentação.
Um agente ZCode executa uma exploração independente em GLM-5.3-Flash e mantém
um relatório rastreável antes da triagem. Um Terra recebe todos os relatórios,
valida os findings difíceis e gerencia uma única onda de correção; Lunas podem
implementar blocos disjuntos em worktrees próprias, sempre com TDD e integração
serializada pelo Terra. O controlador não edita a correção diretamente.

## Aceite

- Cada finding adotado aponta arquivo, símbolo, causa, risco, comportamento
  preservado e teste.
- Cada mudança comportamental possui teste regressivo em ciclo RED/GREEN e
  manifesto curto em `docs/verification/` quando aplicável.
- A PR permanece revisável: até 25 arquivos contados e aproximadamente 800
  linhas manuais, salvo a autorização explícita do pedido para uma única PR;
  nesse caso o excesso é medido e justificado no PR.
- Há revisão independente final no modelo solicitado, testes focados dos
  contratos alterados e um único gate público completo após congelamento do
  escopo quando o cruzamento de projetos o justificar.
