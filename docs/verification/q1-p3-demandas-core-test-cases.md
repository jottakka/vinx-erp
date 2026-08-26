# Casos de teste — q1-p3a-demandas-core

Mapa de verificação da fatia A1 (núcleo backend do módulo Demandas). Provas de integração/worker executam na janela de gate do controller; nesta fatia o critério público é build limpo + unit 23/23 + mapa abaixo.

| # | Caso | Nível | Estado no corte |
|---|------|-------|-----------------|
| 1 | Legalidade de transição: avanço manual legal só em gatilho explícito; checklist/tempo devolvem veredito ilegal; `closed` terminal | Unit (`DemandRulesTests`) | Verde local |
| 2 | Fluxo padrão linear initiation→execution→awaiting_approval→closed e próxima etapa por `order_index` com fechamento no fim | Unit | Verde local |
| 3 | Precedência de roteamento tipo+departamento > um critério > curinga, desempate por ordem/criação, normalização trim+caixa | Unit | Verde local |
| 4 | Checklist 100% exige ≥1 item e todos marcados | Unit | Verde local |
| 5 | Prazo efetivo = mínimo entre manual/SLA/etapa; atraso derivado exclui pausada/fechada e empate não é atraso | Unit | Verde local |
| 6 | Re-iniciação apenas por responsável/rotina/tipo/departamento; nunca título/descrição/prioridade/prazo | Unit | Verde local |
| 7 | Ciclo de vida create→roteia→avança via botão→checklist auto-avança→fecha, com eventos append-only por ciclo | Integração (fixtures Foundation) | RED agendado (gate) |
| 8 | Re-iniciação via troca de responsável eleva ciclo e renova chave de dedupe dos avisos | Integração | RED agendado |
| 9 | Conflito de versão otimista devolve integrity_conflict sem sobrescrever | Integração | RED agendado |
| 10 | RLS fail-closed: sem GUC company nenhuma leitura/escrita atravessa; isolamento multiempresa | Integração | RED agendado |
| 11 | Idempotência: replay equivalente devolve original; payload divergente devolve idempotency_conflict | Integração | RED agendado |
| 12 | Outbox observável: intents composições pós-commit para assigned/stage_advanced/participant_added com chave ciclo-estável | Integração | RED agendado |

Fatias seguintes: aprovação (prepare/bloqueio de entrada em awaiting_approval; confirm claimed/replay encerra; veto reinicia) e worker (avança DUE → depois overdue; eventos idempotentes por chave-gatilho; corte: sem auto-avanço para awaiting_approval). Decisões cobertas pelos pinos unitários da costura (ApprovalCapabilityPolicyPinTests 3/3).
