# Ledger de vocabulário de status — legado → alvo

Template de tradução dos vocabulários de status do legado para os termos do ERP alvo. Os valores-chave abaixo vêm das constraints `CHECK` reais nos fontes do cliente (clone `/tmp/vinx-client`); a coluna **Valor alvo** fica vazia até a readback.

## Regra da célula vazia até a readback

Nenhum **Valor alvo** é preenchido sem readback: rodar primeiro o snapshot e o profiling agregado (`tools/legacy-snapshot/profile-tables.sh`), que lista os valores distintos realmente presentes em produção por coluna configurada. Valores encontrados em produção que não estejam nesta ledger são bloqueio, não inferência: adicioná-los aqui com evidência antes de importar.

## Atenção: chaves mal rotuladas do Kanban

A migração `0056_nomenclatura_etapas_kanban.sql` trocou somente os textos visíveis e manteve as chaves técnicas estáveis:

- chave `execucao` exibe **"Em Iniciação"**
- chave `aguardando_info` exibe **"Em Execução"**

A chave técnica não é confiável como tradução semântica: mapear pelo texto real mostrado ao usuário + leitura de retorno do conteúdo, nunca pelo nome da chave.

## `demandas.status` — `0055_kanban_automatizado.sql`

Chaves ativas no constraint final; chaves históricas (`nova`, `triagem`, `direcionado`, `encaminhado`) foram recolhidas para `execucao` pela própria migração.

| Chave legada | Rótulo visível atual | Origem | Valor alvo | Evidência da readback |
|---|---|---|---|---|
| `execucao` | "Em Iniciação" (mal rotulada desde 0056) | `0056_nomenclatura_etapas_kanban.sql` | | |
| `aguardando_info` | "Em Execução" (mal rotulada desde 0056) | `0056_nomenclatura_etapas_kanban.sql` | | |
| `aguardando_aprovacao` | "Em Aprovação" | `0055_kanban_automatizado.sql` | | |
| `atrasado` | "Atrasado" | `0055_kanban_automatizado.sql` | | |
| `concluido` | "Concluído" | `0055_kanban_automatizado.sql` | | |
| `nova` / `triagem` / `direcionado` / `encaminhado` | históricas (recolhidas a `execucao`) | pré-`0055` | | |

## `demanda_rotina_etapas.status` — `0055_kanban_automatizado.sql`

| Chave legada | Rótulo visível | Valor alvo | Evidência da readback |
|---|---|---|---|
| `execucao` | "Em Iniciação" (mal rotulada) | | |
| `aguardando_info` | "Em Execução" (mal rotulada) | | |
| `aguardando_aprovacao` | "Em Aprovação" | | |

## Outros vocabulários (seeds das constraints)

Preencher após profiling; colunas confirmadas nas migrações indicadas.

| Tabela.coluna | Chaves legadas | Fonte | Valor alvo definido? |
|---|---|---|---|
| `tickets.status` | `aberto`, `em_andamento`, `pendente`, `resolvido`, `fechado`, `cancelado` | `0029_work_center_tickets_tarefas.sql` | |
| `tarefas.status` | `a_fazer`, `em_andamento`, `em_revisao`, `concluida`, `cancelada` | `0029_work_center_tickets_tarefas.sql` | |
| `projetos.status` | `planejado`, `em_andamento`, `pausado`, `concluido`, `cancelado` | `0031_projetos.sql` | |
| `notificacoes_fila.status` | `pendente`, `enviado`, `erro`, `ignorado` | `0029_work_center_tickets_tarefas.sql` (fila externa) | |
