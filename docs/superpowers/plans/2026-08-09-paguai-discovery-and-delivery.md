# Paguai Discovery and Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fechar a descoberta Paguai e entregar V1 em fatias auditáveis, sem iniciar domínio financeiro antes das decisões e contratos necessários.

**Architecture:** A documentação #45 é o contrato canônico. Foundation/Documents/Notifications evoluem como infraestruturas independentes; Paguai consome esses contratos por comandos versionados e eventos pós-commit. Frontend é separado do backend apenas onde o volume combinado ultrapassaria um PR médio ou a UI pode validar contrato por mock.

**Tech Stack:** .NET/EF Core/PostgreSQL, React/TypeScript, OpenAPI, testes públicos de domínio/API/Playwright e GitHub Issues.

## Global Constraints

- Não implementar Paguai nesta branch: somente descoberta, documentação e backlog.
- Sempre exigir empresa explícita, autorização atual, auditoria, versão otimista e idempotência nas mutações financeiras.
- Um PR deve ser dividido acima de 25 arquivos não teste ou 800 linhas manuais, ou quando misturar subsistemas independentes. Migrations, snapshots, lockfiles, configuração, assets e documentação contam como arquivos; somente testes ficam fora dessa contagem. Linhas manuais de testes contam no limiar de 800; somente linhas geradas de migrations, snapshots e lockfiles ficam fora, mas devem ser reportadas.
- Frontend e backend ficam no mesmo PR médio quando têm uma responsabilidade indivisível; caso contrário, usar contrato publicado/mock e PRs separados.
- Todo PR que muda comportamento versiona `docs/verification/issue-<numero>[-be|-fe]-test-cases.md`, com BDD, teste/caminho, camada e o que ele prova. Cenário não coberto fica explícito com justificativa e issue.
- Toda issue contém BDD concreto de sucesso, validação/erro, autorização/tenant, ciclo/exceções e concorrência/idempotência quando aplicável.
- Manter V2 fora da V1: #82, #84, #85 e #87.

---

## Estrutura documental e de issues

| Artefato | Responsabilidade |
| --- | --- |
| `docs/modules/paguai.md` | Glossário, atores, estados, acesso, exceções, BDD e gates de decisão. |
| `docs/superpowers/plans/2026-08-09-paguai-discovery-and-delivery.md` | Ordem de entrega, dependências, divisão de PR e padrão de verificação. |
| #45 | Aceitar o contrato canônico e resolver os sete gates antes de código Paguai. |
| #46 / #93 | Agregado/backend e jornada frontend de solicitação. |
| #83 / #96 | Resolução de política no servidor e administração visual separada. |
| #47 / #23 / #97 | Conferência, alçadas e experiência de decisão. |
| #54 / #98 | Confirmação manual backend e operação financeira frontend. |
| #99, filha de #49 | Projeção Paguai somente leitura no Kanban. |

### Task 1: Publicar a especificação canônica da descoberta

**Files:**
- Create: `docs/modules/paguai.md`
- Create: `docs/superpowers/plans/2026-08-09-paguai-discovery-and-delivery.md`

**Interfaces:**
- Consumes: decisões registradas em #45, #82–#85, #87 e evidência do legado.
- Produces: vocabulário, máquina de estados, matriz de atores e BDD usados por toda issue Paguai.

- [ ] **Step 1: Conferir que cada termo de estado tem uma única definição**

  Verificar que a tabela contém apenas `rascunho`, `em_conferencia`, `devolvido`, `aguardando_aprovacao`, `pronto_para_financeiro`, `pago`, `rejeitado` e `cancelado`; `enviado`, `agendado` e `falha_pagamento` não podem ser estados V1.

- [ ] **Step 2: Validar vínculos de issue no Markdown**

  Run: `rg -n 'https://github.com/jottakka/vinx-erp/issues/(45|82|83|84|85|87)' docs/modules/paguai.md`

  Expected: PASS com todas as issues de decisão e V2 rastreáveis.

- [ ] **Step 3: Validar BDD e escopo sem código**

  Run: `rg -n 'Cenário:|entidade|migration|endpoint|capability' docs/modules/paguai.md`

  Expected: os cenários de descoberta aparecem; não há instrução para criar entidade, migration, endpoint ou capability.

- [ ] **Step 4: Commit**

  ```bash
  git add docs/modules/paguai.md docs/superpowers/plans/2026-08-09-paguai-discovery-and-delivery.md
  git commit -m "docs: define descoberta canônica do Paguai"
  ```

### Task 2: Normalizar a hierarquia e os BDDs das issues existentes

**Files:**
- Modify: GitHub #4, #22, #23, #26, #27, #28, #39, #45, #46, #47, #49, #52, #54, #55, #82, #84, #85 e #87

**Interfaces:**
- Consumes: `docs/modules/paguai.md`.
- Produces: issues sem estados V1 contraditórios, ownership único e BDDs completos.

- [ ] **Step 1: Corrigir a fonte canônica de V1**

  Atualizar #4, #45 e #54 para remover `enviado`, `agendado` e `falha_pagamento` da V1. #54 passa a ser fila e confirmação manual integral; #84 permanece dono de agendamento/tentativas/falha.

- [ ] **Step 2: Definir ownership sem sobreposição**

  Registrar em #22 que ela é contrato/coordenação de jornada; #46 é backend de agregado; #93 é lista/formulário frontend. Registrar em #47 que conferência/ciclo precedem #23, que resolve alçadas.

- [ ] **Step 3: Adicionar BDD de caminhos materiais**

  Para cada issue, acrescentar cenários específicos de sucesso, erro/validação, tenant/autorização e concorrência ou idempotência quando a issue tiver comando/worker. Em issues documentais, cobrir contradição, transição inválida e rastreabilidade de V2.

- [ ] **Step 4: Conferir rastreabilidade**

  Run: `gh issue view 45 --repo jottakka/vinx-erp --comments`

  Expected: a issue aponta para o documento canônico e para cada issue V1/V2 correspondente.

### Task 3: Criar fatias faltantes, já com BDD completo

**Files:**
- Modify: GitHub Issues (novas filhas de #4 e #49)

**Interfaces:**
- Consumes: contratos backend #83, #47/#23 e #54; eventos #26; projeção #49.
- Produces: filas de trabalho frontend/kanban que não incham os PRs backend.

- [ ] **Step 1: Criar frontend de administração de política**

  Issue criada: #96. Separar da implementação #83 porque API, resolução de precedência e tela administrativa tendem a exceder PR médio. Depender do contrato #83 e exigir mock de erro/concorrência.

- [ ] **Step 2: Criar caixa frontend de conferência e aprovação**

  Issue criada: #97. Concentrar confirmação individual de fatura, devolução com motivo/descrição, decisão de alçada/MFA e estados de erro; depender de #47 e #23.

- [ ] **Step 3: Criar fila frontend de Financeiro**

  Issue criada: #98. Concentrar KPIs, filtros, separação entre `pagar até` e data efetiva, comprovante e confirmação manual; depender de #54 e não mostrar agendamento V1.

- [ ] **Step 4: Criar projeção Paguai read-only no Kanban**

  Issue criada: #99, filha de #49; card é atualizável por evento, abre deep-link autorizado e não aceita movimento financeiro.

- [ ] **Step 5: Conferir corpo de cada issue nova**

  Run: `gh issue view <numero> --repo jottakka/vinx-erp`

  Expected: escopo, fora de escopo, dependências, estimativa, manifesto de testes e BDD completo aparecem no corpo.

## Ordem de entrega após a descoberta

1. #45 e seus sete gates; em paralelo, #52, #39 e #26.
2. #46 backend e #93 frontend por contrato/mock.
3. #83 backend e a tela de política separada.
4. #47, #23 e #97 (caixa frontend de conferência/aprovação).
5. #54 e #98 (fila frontend Financeiro).
6. #27, #28, #99 (projeção Kanban filha de #49) e, por último, #55.

Cada implementação recebe plano próprio antes de código. #82, #84, #85 e #87 são V2 e não entram nesta sequência.

## Auto-revisão

- Cobertura: o contrato inclui os pedidos de alertas, visualização registrada, fatura/comprovante, grupos/pessoas, devolução, `pagar até`, WhatsApp V1 e V2s explicitamente adiadas.
- Limites: não há entidade, migration, endpoint ou capability planejados nesta branch de descoberta.
- Consistência: todos os links V2 são concentrados em #82, #84, #85 e #87; `agendado` não é usado como estado V1.
- Sem placeholders: a sequência de issues identifica dono, dependência e resultado verificável; planos de implementação futuros serão específicos por slice.
