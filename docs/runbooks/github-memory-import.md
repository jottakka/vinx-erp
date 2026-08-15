# Manifesto de importação de memória do GitHub

Verificado: 2026-08-10
Repositório: [jottakka/vinx-erp](https://github.com/jottakka/vinx-erp)
Status: snapshot curado; o GitHub continua sendo a fonte viva de status e prioridade.

Este arquivo é um manifesto revisável, não um espelho do GitHub. Git,
Markdown, testes, ADRs e issues vivas continuam canônicos. O manifesto guarda
metadados e sínteses curtas; não guarda corpos, comentários, anexos, prompts,
segredos, credenciais, PII, conteúdo médico ou material de quality/private.

## Protocolo dry-run

1. Confirme checkout, branch e status com git rev-parse --show-toplevel,
   git branch --show-current e git status --short.
2. Use gh autenticado em modo somente leitura para coletar candidatos.
3. Permita apenas repositório, número, título, estado, URL, updatedAt,
   checked_at, resumo seguro curto e caminho canônico.
4. Classifique como link, nota, issue operacional local ou rejeitado.
   Deduplicate por github:jottakka/vinx-erp:<tipo>:<número>.
5. Revise este manifesto antes de escrever eventos pjm. Reconsulte issues
   abertas quando a tarefa começar; o snapshot não altera o GitHub.

## Contexto selecionado

| Fonte | Estado no check | Tratamento local |
| --- | --- | --- |
| [Issue #108 — Product V1 + Agentic track](https://github.com/jottakka/vinx-erp/issues/108) | Open | Nota de rastreamento: coordena as trilhas V1 e agentic; ordem e detalhes continuam na issue viva. |
| [Issue #127 — Agent Harness](https://github.com/jottakka/vinx-erp/issues/127) | Open | A prova sem host foi removida na #207; runtime volta somente com consumidor de produção, contexto confiável separado de input externo e SDK oficial. |
| [Issue #21 — sessões V1 e revogação global](https://github.com/jottakka/vinx-erp/issues/21) | Open | Nota: a base EF/admissão existe, mas o gate HTTP e revoke-all ainda são pendências. |
| [Issue #40 — plataforma MCP](https://github.com/jottakka/vinx-erp/issues/40) | Open | Nota: autenticação, catálogo e auditoria MCP são uma capacidade separada e não substituem o núcleo da harness. |
| [Issue #110 — bootstrap autenticado e feature flags](https://github.com/jottakka/vinx-erp/issues/110) | Open | Issue operacional local: não implementar isoladamente; validar dependências de autorização, flags e OpenAPI antes. |

## Fontes apenas por link

Mantenha como fonte canônica, sem duplicar corpo ou comentários:
[identity #3](https://github.com/jottakka/vinx-erp/issues/3),
[capabilities #19](https://github.com/jottakka/vinx-erp/issues/19),
[RLS #20](https://github.com/jottakka/vinx-erp/issues/20),
[sessions #44](https://github.com/jottakka/vinx-erp/issues/44),
[all-authorized #68](https://github.com/jottakka/vinx-erp/issues/68),
[whoAmI #125](https://github.com/jottakka/vinx-erp/issues/125),
[server approval #126](https://github.com/jottakka/vinx-erp/issues/126),
[deploy #70](https://github.com/jottakka/vinx-erp/issues/70) e
[backup #71](https://github.com/jottakka/vinx-erp/issues/71).

Issues downstream, como #114 e #115, não entram neste piloto para evitar
duplicação e estado rapidamente obsoleto. Leia-as diretamente quando o slice
as exigir.

## Atualização

Use gh issue view NUMERO --repo jottakka/vinx-erp e abra o Markdown canônico
antes de atualizar este manifesto. Se o GitHub estiver indisponível, mantenha
o snapshot datado e não trate seu estado antigo como atual.
