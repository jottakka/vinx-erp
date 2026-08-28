# Ledger de classificação de tabelas legadas

Insumo: `docs/analysis/client-comparison/luna-capability-matrix.md` (inventário de 102 tabelas) e clone somente-leitura em `/tmp/vinx-client` (`supabase/migrations/*.sql`, `schema.sql`). Nenhum dado de cliente entra neste repositório: esta ledger contém apenas metadados de origem.

## Regra da célula vazia até a readback

A coluna **Classificação** permanece **vazia** para toda tabela até que o responsável confirme a leitura de retorno (readback) do snapshot imutável de produção capturado por `tools/legacy-snapshot/`. Nada é preenchido por inferência a partir dos arquivos do repositório: o clone só prova o que foi criado no schema, não o estado ou uso real em produção. Ao confirmar cada célula, registrar data e evidência (tabela/contagem do snapshot) na linha.

Valores permitidos na classificação:

| Valor | Significado |
|---|---|
| `canonical` | Migrar como dado canônico para o ERP alvo (com ledger de posse/identidade/status aplicáveis). |
| `restricted-archive` | Não migra; preservar apenas como arquivo somente-leitura restrito no legado (acesso controlado pelo responsável). |
| `discard` | Não migrar e não arquivar; abandono registrado com justificativa. |

Provisões transversais já confirmadas pelas fontes:

- As 102 tabelas têm cobertura de política RLS no fonte (`luna-capability-matrix.md` §3); isso não substitui a leitura de retorno das políticas reais do banco.
- Filas externas (`mail_outbound_queue`, `notificacoes_fila`) nunca são importadas como trabalho ativo; itens pendentes ficam drenados no sistema antigo antes do congelamento.
- `kanban_colunas`/`kanban_cartoes` são o modelo Kanban antigo; a ordem Demandas-vs-Customers é decidida pela atividade real medida no snapshot.

## Tabelas (102)

| Tabela | Fonte de criação | Serviços que usam (`src/services`) | Nota | Classificação |
|---|---|---|---|---|
| `arquivos` | `migrations/0026_produtividade_arquivos.sql` | `arquivosService.ts` ||
| `atividades` | `schema.sql` | — ||
| `bancos` | `migrations/0024_tabelas_auxiliares_rh.sql` | `listasService.ts` ||
| `cargo_modulos` | `migrations/0016_rbac.sql` | `cargosService.ts` ||
| `cargos` | `migrations/0016_rbac.sql` | `cargosService.ts` ||
| `cargos_funcao` | `migrations/0022_listas_rh.sql` | `listasService.ts`, `rhEstruturaService.ts` ||
| `centros_custo` | `migrations/0022_listas_rh.sql` | `listasService.ts`, `rhEstruturaService.ts` ||
| `chamadas` | `migrations/0044_chamadas_fase1.sql` | `chamadasService.ts` ||
| `chat_anexos` | `migrations/0043_chat_fase1.sql` | `chatService.ts` ||
| `chat_conversas` | `migrations/0043_chat_fase1.sql` | `chatService.ts`, `dashboardColaboradorService.ts` ||
| `chat_mensagens` | `migrations/0043_chat_fase1.sql` | `chatService.ts`, `dashboardColaboradorService.ts` ||
| `chat_participantes` | `migrations/0043_chat_fase1.sql` | `chatService.ts`, `dashboardColaboradorService.ts` ||
| `chat_reacoes` | `migrations/0043_chat_fase1.sql` | `chatService.ts` ||
| `cliente_documentos` | `migrations/0010_cliente_documentos.sql` | `documentosService.ts` ||
| `cliente_enderecos` | `migrations/0009_cliente_enderecos.sql` | `enderecosService.ts` ||
| `cliente_representantes` | `migrations/0013_clientes_pj.sql` | `representantesService.ts` ||
| `clientes` | `schema.sql` | `clientesService.ts` | exemplo guiado do importador (clientes → Customers staging)|
| `condicoes_pagamento` | `migrations/0007_listas_cliente.sql` | `listasService.ts` ||
| `contatos` | `schema.sql` | `contatosService.ts` ||
| `demanda_envolvidos` | `migrations/0055_kanban_automatizado.sql` | `demandasService.ts` ||
| `demanda_rotina_etapas` | `migrations/0055_kanban_automatizado.sql` | `demandasService.ts` ||
| `demanda_rotinas` | `migrations/0055_kanban_automatizado.sql` | `demandasService.ts` ||
| `demandas` | `migrations/0033_demandas.sql` | `demandasService.ts` ||
| `departamentos` | `migrations/0022_listas_rh.sql` | `listasService.ts`, `rhEstruturaService.ts` ||
| `empresas_grupo` | `schema.sql` | `empresasGrupoService.ts` | insumo para a ledger de posse/empresa|
| `equipe` | `migrations/0015_equipe.sql` | `listasService.ts` ||
| `etapas_pipeline` | `schema.sql` | — ||
| `eventos` | `migrations/0030_agenda_eventos_feriados.sql` | `dashboardColaboradorService.ts`, `eventosService.ts` ||
| `feed_canais` | `migrations/0040_feed_corporativo.sql` | `feedService.ts` ||
| `feed_comentarios` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feed_enquete_votos` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feed_leituras` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feed_publicacoes` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feed_reacoes` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feed_salvos` | `migrations/0040_feed_corporativo.sql` | `dashboardColaboradorService.ts`, `feedService.ts` ||
| `feriados` | `migrations/0030_agenda_eventos_feriados.sql` | `feriadosService.ts` ||
| `filiais` | `migrations/0023_estrutura_organizacional.sql` | `listasService.ts` ||
| `funcionarios` | `migrations/0020_funcionarios.sql` | `funcionariosService.ts` ||
| `grupos_economicos` | `migrations/0013_clientes_pj.sql` | `gruposService.ts` ||
| `kanban_cartoes` | `migrations/0027_produtividade_kanban.sql` | `dashboardColaboradorService.ts`, `kanbanService.ts` ||
| `kanban_colunas` | `migrations/0027_produtividade_kanban.sql` | `dashboardColaboradorService.ts`, `kanbanService.ts` ||
| `leads` | `schema.sql` | — ||
| `mail_account_permissions` | `migrations/0045_email_fase0.sql` | — ||
| `mail_accounts` | `migrations/0045_email_fase0.sql` | `dashboardColaboradorService.ts`, `emailService.ts` ||
| `mail_attachments` | `migrations/0045_email_fase0.sql` | — ||
| `mail_audit_logs` | `migrations/0045_email_fase0.sql` | — ||
| `mail_entity_links` | `migrations/0045_email_fase0.sql` | — ||
| `mail_folders` | `migrations/0045_email_fase0.sql` | `dashboardColaboradorService.ts`, `emailService.ts` ||
| `mail_labels` | `migrations/0045_email_fase0.sql` | `emailService.ts` ||
| `mail_message_bodies` | `migrations/0045_email_fase0.sql` | `emailService.ts` ||
| `mail_message_labels` | `migrations/0045_email_fase0.sql` | `emailService.ts` ||
| `mail_messages` | `migrations/0045_email_fase0.sql` | `dashboardColaboradorService.ts`, `emailService.ts` ||
| `mail_outbound_queue` | `migrations/0045_email_fase0.sql` | `dashboardColaboradorService.ts`, `emailService.ts` | fila externa: nunca importar itens pendentes como trabalho ativo|
| `mail_recipients` | `migrations/0045_email_fase0.sql` | — ||
| `mail_rules` | `migrations/0045_email_fase0.sql` | — ||
| `mail_sync_state` | `migrations/0045_email_fase0.sql` | — ||
| `mail_threads` | `migrations/0045_email_fase0.sql` | — ||
| `motivos_perda` | `schema.sql` | — ||
| `negociacoes` | `schema.sql` | — ||
| `notas` | `migrations/0025_produtividade_tarefas_notas.sql` | `notasService.ts` ||
| `notificacoes_fila` | `migrations/0029_work_center_tickets_tarefas.sql` | — | fila externa: nunca importar itens pendentes como trabalho ativo|
| `notificacoes_usuario` | `migrations/0055_kanban_automatizado.sql` | `dashboardColaboradorService.ts`, `notificacoesService.ts` | fila/notificação de usuário: avaliar descarte na readback|
| `origens` | `schema.sql` | `listasService.ts` ||
| `painel_usuario` | `migrations/0032_painel_usuario.sql` | `painelService.ts` ||
| `perfil_empresas` | `schema.sql` | `usuariosService.ts` | insumo para a ledger de posse/empresa|
| `perfis` | `schema.sql` | `perfilService.ts`, `usuariosService.ts` | dependência da ledger de identidade (perfil_id)|
| `perfis_cliente` | `migrations/0007_listas_cliente.sql` | `listasService.ts` ||
| `profissoes` | `migrations/0014_profissoes.sql` | `listasService.ts` ||
| `projeto_membros` | `migrations/0054_projetos_dono_membros.sql` | `projetosService.ts` ||
| `projetos` | `migrations/0031_projetos.sql` | `dashboardColaboradorService.ts`, `projetosService.ts` ||
| `regimes` | `migrations/0024_tabelas_auxiliares_rh.sql` | `listasService.ts` ||
| `rh_alcadas` | `migrations/0046_rh_estrutura.sql` | `rhEstruturaService.ts` ||
| `rh_ausencias` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_avaliacoes` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_beneficio_colaborador` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_beneficios` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_candidatos` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_contratos` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_desligamentos` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_epi_entregas` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_epis` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_exames` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_ferias` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_incidentes` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_medidas` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_modelos_documento` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_movimentacoes` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_ocorrencias` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_pdi` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_ponto` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_treinamento_participantes` | `migrations/0047_rh_ondas_bcde.sql` | — ||
| `rh_treinamentos` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `rh_vagas` | `migrations/0047_rh_ondas_bcde.sql` | `rhOperacionalService.ts` ||
| `segmentos` | `migrations/0003_segmentos.sql` | `segmentosService.ts` ||
| `setores` | `migrations/0023_estrutura_organizacional.sql` | `listasService.ts` ||
| `tarefas` | `migrations/0025_produtividade_tarefas_notas.sql` | `dashboardColaboradorService.ts`, `projetosService.ts`, `tarefasService.ts` ||
| `tickets` | `migrations/0029_work_center_tickets_tarefas.sql` | `dashboardColaboradorService.ts`, `ticketsService.ts` ||
| `tipos_contrato` | `migrations/0024_tabelas_auxiliares_rh.sql` | `listasService.ts` ||
| `tipos_vinculo` | `migrations/0024_tabelas_auxiliares_rh.sql` | `listasService.ts` ||
| `unidades` | `migrations/0023_estrutura_organizacional.sql` | `listasService.ts` ||
| `usuario_modulos` | `migrations/0016_rbac.sql` | `usuariosService.ts` ||
| `work_historico` | `migrations/0029_work_center_tickets_tarefas.sql` | `projetosService.ts`, `tarefasService.ts`, `ticketsService.ts` ||
