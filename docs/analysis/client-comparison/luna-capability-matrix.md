# Capability matrix: self-built Supabase-backed React ERP

Audit scope: `/tmp/vinx-client`, read-only source inspection on 2026-08-26.

The requested `src/services/*.ts` count is 33, not 34. `find src/services -maxdepth 1 -type f -name '*.ts'` found the 33 files listed below; no 34th matching file exists. The SQL inventory covers `supabase/migrations/*.sql` and `supabase/schema.sql` only. `supabase/rbac_schema.sql` is intentionally excluded because it was not part of the requested grep scope; it repeats part of the RBAC bootstrap.

The service table is based on direct Supabase calls in each file. `dashboardColaboradorService.ts` is called out as an aggregator because it delegates to other services rather than issuing its own Supabase query. No `CREATE VIEW` or `CREATE MATERIALIZED VIEW` was found in the requested SQL scope, so the DB targets below are tables or RPCs, not views.

## 1. Service capability matrix

| Service file | Tables / views touched | RPCs | Edge Function in `supabase/functions` | Storage bucket(s) | What the service does |
|---|---|---|---|---|---|
| `arquivosService.ts` | `arquivos` | — | No | `arquivos` | Lists and manages the hierarchical file/folder area, uploads and removes objects, and creates signed download URLs. |
| `cargosService.ts` | `cargos`, `cargo_modulos` | — | No | — | CRUD for cargos and their module permissions. |
| `chamadasService.ts` | `chamadas` | — | No | — | Creates/reads call records and transitions audio/video call status and timestamps. |
| `chatService.ts` | `chat_conversas`, `chat_participantes`, `chat_mensagens`, `chat_anexos`, `chat_reacoes` | — | No | `chat` | Lists and mutates conversations, participants, messages, reactions, read state, and message attachments. |
| `clientesService.ts` | `clientes` | — | No | — | Company-scoped CRM customer search, create/update/delete, and photo-path updates. |
| `contatosService.ts` | `contatos` | — | No | — | Replaces and lists contact people belonging to a client. |
| `dashboardColaboradorService.ts` | Indirectly delegates to `mail_accounts`, `mail_folders`, `mail_messages`, `mail_outbound_queue`, `chat_conversas`, `chat_participantes`, `chat_mensagens`, `tickets`, `eventos`, `kanban_colunas`, `kanban_cartoes`, `projetos`, `tarefas`, `notificacoes_usuario`, `feed_publicacoes`, `feed_reacoes`, `feed_comentarios`, `feed_leituras`, `feed_enquete_votos`, and `feed_salvos` | No direct RPC | No Edge Function on the dashboard load path; delegated service APIs may expose their own functions | None on the dashboard load path | Aggregates email, chat, tickets, agenda, Kanban, projects, tasks, notifications, and corporate-feed summaries with per-source failure isolation. |
| `demandasService.ts` | `demandas`, `demanda_envolvidos`, `demanda_rotinas`, `demanda_rotina_etapas` | `processar_automacoes_demandas`, `demanda_pode_trocar_responsavel`, `concluir_etapa_demanda`, `retomar_demanda_atrasada` | No | — | CRUD for demands, people involved, configurable routines/stages, and the automated stage-transition workflow. |
| `documentosService.ts` | `cliente_documentos` | — | No | `clientes` | Uploads client photos/documents, records document metadata, removes objects/records, and creates signed URLs. |
| `emailService.ts` | `mail_accounts`, `mail_messages`, `mail_message_bodies`, `mail_outbound_queue`, `mail_folders`, `mail_labels`, `mail_message_labels` | — | `email-account-test`, `email-imap-action`, `email-message-body`, `email-smtp-send`, `email-imap-sync` | `email` | Manages mailbox accounts, message listing/body loading, labels, attachment upload, outbound queueing, SMTP send, IMAP actions, and synchronization. |
| `empresasGrupoService.ts` | `empresas_grupo` | — | No | — | Lists group companies for selection/summary. The `/empresas-grupo` page currently bypasses this service and queries Supabase directly. |
| `enderecosService.ts` | `cliente_enderecos` | — | No | — | Lists and replaces a client's addresses. |
| `eventosService.ts` | `eventos` | — | No | — | Lists, creates, updates, and deletes agenda events with participant/owner data. |
| `feedService.ts` | `feed_canais`, `feed_publicacoes`, `feed_reacoes`, `feed_comentarios`, `feed_leituras`, `feed_enquete_votos`, `feed_salvos` | — | No | `feed` | Implements the corporate feed: channels, publications, comments, reactions, saved items, read confirmations, polls, and attachments. |
| `feriadosService.ts` | `feriados` | `garantir_feriados` | No | — | Lists holidays and idempotently persists a fetched holiday set through the RPC. |
| `funcionariosService.ts` | `funcionarios` | — | No | `funcionarios` | CRUD for employees plus signed profile-photo and document uploads. |
| `gruposService.ts` | `grupos_economicos` | — | No | — | Lists, finds, and creates economic groups used by PJ customers. |
| `kanbanService.ts` | `kanban_colunas`, `kanban_cartoes` | — | No | — | CRUD and ordering for the older generic Kanban columns/cards model. |
| `listasService.ts` | Dynamic table argument; concrete exports cover `perfis_cliente`, `condicoes_pagamento`, `origens`, `profissoes`, `equipe`, `cargos_funcao`, `departamentos`, `centros_custo`, `filiais`, `unidades`, `setores`, `tipos_vinculo`, `tipos_contrato`, `regimes`, and `bancos` | — | No | — | Provides reusable list-table CRUD for CRM and RH reference catalogs. |
| `notasService.ts` | `notas` | — | No | — | CRUD for personal/work notes. |
| `notificacoesService.ts` | `notificacoes_usuario` | — | No | — | Lists the user's notifications and marks them read/deletes them. |
| `painelService.ts` | `painel_usuario` | — | No | — | Reads and updates the user's dashboard/panel preferences. |
| `perfilService.ts` | `perfis` | — | No | — | Reads a user's role/papel. |
| `permissoesService.ts` | No direct table query | `meus_acessos` | No | — | Loads the current user's module/action authorization snapshot. |
| `pessoasService.ts` | No direct table query | `colegas_empresa` | No | — | Loads the people/colleagues visible for a company through the RPC. |
| `projetosService.ts` | `projetos`, `work_historico`, `tarefas`, `projeto_membros` | — | No | — | CRUD for projects, history, project-task summaries, and project membership. |
| `representantesService.ts` | `cliente_representantes` | — | No | — | Replaces and lists legal representatives for a PJ client. |
| `rhEstruturaService.ts` | Dynamic table argument; concrete exports use `cargos_funcao`, `centros_custo`, and `departamentos`; also `rh_alcadas` | — | No | — | CRUD for RH structural catalogs and approval thresholds/authority rules. |
| `rhOperacionalService.ts` | Dynamic table argument; concrete exports use `rh_modelos_documento`, `rh_contratos`, `rh_movimentacoes`, `rh_ferias`, `rh_ausencias`, `rh_beneficios`, `rh_beneficio_colaborador`, `rh_ponto`, `rh_treinamentos`, `rh_avaliacoes`, `rh_pdi`, `rh_exames`, `rh_epis`, `rh_epi_entregas`, `rh_incidentes`, `rh_vagas`, `rh_candidatos`, `rh_ocorrencias`, `rh_medidas`, and `rh_desligamentos` | — | No | — | Supplies generic CRUD for the RH operational waves B–E. It does not touch the separately created `rh_treinamento_participantes` table. |
| `segmentosService.ts` | `segmentos` | — | No | — | CRUD/listing for customer segments. |
| `tarefasService.ts` | `tarefas`, `work_historico` | — | No | — | CRUD for tasks, status transitions, and task history. |
| `ticketsService.ts` | `tickets`, `work_historico` | — | No | — | CRUD for support/work-center tickets and ticket history. |
| `usuariosService.ts` | `perfis`, `perfil_empresas`, `usuario_modulos` | — | `criar-usuario`, `resetar-senha-usuario` | — | Creates/resets users through Edge Functions and manages profile, company-access, cargo, and module exceptions. |

Edge Functions present but not invoked by any `src/services/*.ts` file: `consulta-cnpj` (the CNPJ helper is used outside this service directory). Storage buckets referenced by services are `clientes`, `arquivos`, `funcionarios`, `chat`, `feed`, and `email`.

## 2. Full table inventory

This is the complete `CREATE TABLE` inventory from the requested files. There are 103 matching occurrences and 102 unique table names. `tarefas` is intentionally shown twice because both migrations contain a `CREATE TABLE` occurrence for it.

| Migration/source | Tables created by `CREATE TABLE` |
|---|---|
| `0003_segmentos.sql` | `segmentos` |
| `0007_listas_cliente.sql` | `perfis_cliente`, `condicoes_pagamento` |
| `0009_cliente_enderecos.sql` | `cliente_enderecos` |
| `0010_cliente_documentos.sql` | `cliente_documentos` |
| `0013_clientes_pj.sql` | `grupos_economicos`, `cliente_representantes` |
| `0014_profissoes.sql` | `profissoes` |
| `0015_equipe.sql` | `equipe` |
| `0016_rbac.sql` | `cargos`, `cargo_modulos`, `usuario_modulos` |
| `0020_funcionarios.sql` | `funcionarios` |
| `0022_listas_rh.sql` | `cargos_funcao`, `departamentos`, `centros_custo` |
| `0023_estrutura_organizacional.sql` | `filiais`, `unidades`, `setores` |
| `0024_tabelas_auxiliares_rh.sql` | `tipos_vinculo`, `tipos_contrato`, `regimes`, `bancos` |
| `0025_produtividade_tarefas_notas.sql` | `tarefas`, `notas` |
| `0026_produtividade_arquivos.sql` | `arquivos` |
| `0027_produtividade_kanban.sql` | `kanban_colunas`, `kanban_cartoes` |
| `0029_work_center_tickets_tarefas.sql` | `tickets`, `tarefas` (second `CREATE TABLE` occurrence), `work_historico`, `notificacoes_fila` |
| `0030_agenda_eventos_feriados.sql` | `eventos`, `feriados` |
| `0031_projetos.sql` | `projetos` |
| `0032_painel_usuario.sql` | `painel_usuario` |
| `0033_demandas.sql` | `demandas` |
| `0040_feed_corporativo.sql` | `feed_canais`, `feed_publicacoes`, `feed_comentarios`, `feed_reacoes`, `feed_leituras`, `feed_salvos`, `feed_enquete_votos` |
| `0043_chat_fase1.sql` | `chat_conversas`, `chat_participantes`, `chat_mensagens`, `chat_reacoes`, `chat_anexos` |
| `0044_chamadas_fase1.sql` | `chamadas` |
| `0045_email_fase0.sql` | `mail_accounts`, `mail_account_permissions`, `mail_folders`, `mail_threads`, `mail_messages`, `mail_message_bodies`, `mail_recipients`, `mail_attachments`, `mail_labels`, `mail_message_labels`, `mail_rules`, `mail_outbound_queue`, `mail_sync_state`, `mail_entity_links`, `mail_audit_logs` |
| `0046_rh_estrutura.sql` | `rh_alcadas` |
| `0047_rh_ondas_bcde.sql` | `rh_modelos_documento`, `rh_contratos`, `rh_movimentacoes`, `rh_ferias`, `rh_ausencias`, `rh_beneficios`, `rh_beneficio_colaborador`, `rh_ponto`, `rh_treinamentos`, `rh_treinamento_participantes`, `rh_avaliacoes`, `rh_pdi`, `rh_exames`, `rh_epis`, `rh_epi_entregas`, `rh_incidentes`, `rh_vagas`, `rh_candidatos`, `rh_ocorrencias`, `rh_medidas`, `rh_desligamentos` |
| `0054_projetos_dono_membros.sql` | `projeto_membros` |
| `0055_kanban_automatizado.sql` | `demanda_rotinas`, `demanda_rotina_etapas`, `demanda_envolvidos`, `notificacoes_usuario` |
| `schema.sql` | `empresas_grupo`, `perfis`, `perfil_empresas`, `origens`, `motivos_perda`, `etapas_pipeline`, `clientes`, `contatos`, `leads`, `negociacoes`, `atividades` |

Migrations present in the directory but without a `CREATE TABLE` occurrence in the requested scan are still covered in the RLS table below. The numeric sequence jumps from `0027` to `0029`; there is no `0028_*.sql` file in this checkout.

## 3. RLS policy coverage

The table below records whether each migration contains the literal `create policy` and the policy target tables. `storage.objects` is shown separately as a Supabase Storage policy target; it is not one of the 102 application tables above. Dynamic `DO` blocks in `0045` and `0047` were expanded from their explicit table arrays when checking coverage.

| Migration/source | Creates policies? | Policy target tables |
|---|---|---|
| `0002_clientes_endereco.sql` | No | — |
| `0003_segmentos.sql` | Yes | `segmentos` |
| `0004_segmentos_lista.sql` | No | — |
| `0005_clientes_codigo.sql` | No | — |
| `0006_clientes_doc_digits.sql` | No | — |
| `0007_listas_cliente.sql` | Yes | `perfis_cliente`, `condicoes_pagamento` |
| `0008_clientes_pf.sql` | No | — |
| `0009_cliente_enderecos.sql` | Yes | `cliente_enderecos` |
| `0010_cliente_documentos.sql` | Yes | `cliente_documentos`, `storage.objects` (`clientes`) |
| `0011_clientes_cpf_unico.sql` | No | — |
| `0012_origens_ativo.sql` | No | — |
| `0013_clientes_pj.sql` | Yes | `grupos_economicos`, `cliente_representantes` |
| `0014_profissoes.sql` | Yes | `profissoes` |
| `0015_equipe.sql` | Yes | `equipe` |
| `0016_rbac.sql` | Yes | `cargos`, `cargo_modulos`, `usuario_modulos` |
| `0017_clientes_rls_modulo.sql` | Yes | `clientes` |
| `0018_rbac_4acoes.sql` | No | — |
| `0019_clientes_rls_4acoes.sql` | Yes | `clientes` |
| `0020_funcionarios.sql` | Yes | `funcionarios`, `storage.objects` (`funcionarios`) |
| `0021_funcionarios_admissao.sql` | No | — |
| `0022_listas_rh.sql` | Yes | `cargos_funcao`, `departamentos`, `centros_custo` |
| `0023_estrutura_organizacional.sql` | Yes | `filiais`, `unidades`, `setores` |
| `0024_tabelas_auxiliares_rh.sql` | Yes | `tipos_vinculo`, `tipos_contrato`, `regimes`, `bancos` |
| `0025_produtividade_tarefas_notas.sql` | Yes | `tarefas`, `notas` |
| `0026_produtividade_arquivos.sql` | Yes | `arquivos`, `storage.objects` (`arquivos`) |
| `0027_produtividade_kanban.sql` | Yes | `kanban_colunas`, `kanban_cartoes` |
| `0029_work_center_tickets_tarefas.sql` | Yes | `tickets`, `tarefas`, `work_historico`, `notificacoes_fila` |
| `0030_agenda_eventos_feriados.sql` | Yes | `eventos`, `feriados` |
| `0031_projetos.sql` | Yes | `projetos` |
| `0032_painel_usuario.sql` | Yes | `painel_usuario` |
| `0033_demandas.sql` | Yes | `demandas` |
| `0034_serpro_campos_pj.sql` | No | — |
| `0035_empresas_grupo_cadastro.sql` | No | — |
| `0036_funcionarios_importacao.sql` | No | — |
| `0037_funcionarios_status_automatico.sql` | No | — |
| `0038_agenda_fase1.sql` | No | — |
| `0039_projetos_fase1.sql` | No | — |
| `0040_feed_corporativo.sql` | Yes | `feed_canais`, `feed_publicacoes`, `feed_comentarios`, `feed_reacoes`, `feed_leituras`, `feed_salvos`, `feed_enquete_votos`, `storage.objects` (`feed`) |
| `0041_tarefas_fase1.sql` | No | — |
| `0042_notas_fase1.sql` | No | — |
| `0043_chat_fase1.sql` | Yes | `chat_conversas`, `chat_participantes`, `chat_mensagens`, `chat_reacoes`, `chat_anexos`, `storage.objects` (`chat`) |
| `0044_chamadas_fase1.sql` | Yes | `chamadas` |
| `0045_email_fase0.sql` | Yes | `mail_accounts`, `mail_account_permissions`, `mail_folders`, `mail_threads`, `mail_messages`, `mail_message_bodies`, `mail_recipients`, `mail_attachments`, `mail_labels`, `mail_message_labels`, `mail_rules`, `mail_outbound_queue`, `mail_sync_state`, `mail_entity_links`, `mail_audit_logs`, `storage.objects` (`email`) |
| `0046_rh_estrutura.sql` | Yes | `rh_alcadas` |
| `0047_rh_ondas_bcde.sql` | Yes | `rh_modelos_documento`, `rh_contratos`, `rh_movimentacoes`, `rh_ferias`, `rh_ausencias`, `rh_beneficios`, `rh_beneficio_colaborador`, `rh_ponto`, `rh_treinamentos`, `rh_treinamento_participantes`, `rh_avaliacoes`, `rh_pdi`, `rh_exames`, `rh_epis`, `rh_epi_entregas`, `rh_incidentes`, `rh_vagas`, `rh_candidatos`, `rh_ocorrencias`, `rh_medidas`, `rh_desligamentos` (generated in a loop) |
| `0048_agenda_pessoal.sql` | Yes | `eventos` |
| `0049_tarefas_do_usuario.sql` | Yes | `tarefas` |
| `0050_demandas_do_usuario.sql` | Yes | `demandas` |
| `0051_chat_do_usuario.sql` | Yes | `chat_conversas`, `chat_participantes`, `chat_mensagens`, `chat_reacoes`, `chat_anexos` |
| `0052_email_do_usuario.sql` | Yes | `mail_accounts` |
| `0053_feed_mural_grupo.sql` | Yes | `feed_canais`, `feed_publicacoes`, `feed_comentarios`, `feed_reacoes`, `feed_leituras`, `feed_salvos`, `feed_enquete_votos` |
| `0054_projetos_dono_membros.sql` | Yes | `projetos`, `projeto_membros`, `tarefas` |
| `0055_kanban_automatizado.sql` | Yes | `demanda_rotinas`, `demanda_rotina_etapas`, `demanda_envolvidos`, `notificacoes_usuario`, `demandas` |
| `0056_nomenclatura_etapas_kanban.sql` | No | — |
| `0057_email_correcao_integral.sql` | Yes | `mail_outbound_queue`, `storage.objects` (`email`) |
| `0058_usuario_colaborador_unico.sql` | No | — |
| `schema.sql` | Yes | `empresas_grupo`, `perfis`, `perfil_empresas`, `origens`, `motivos_perda`, `etapas_pipeline`, `clientes`, `contatos`, `leads`, `negociacoes`, `atividades` |

### Tables without policies

None in the requested inventory: all 102 unique application tables have at least one policy target across the migrations and/or `schema.sql`. This is source-level policy coverage, not a runtime readback of a deployed Supabase project. The report also found Storage policies for every service-referenced bucket: `clientes`, `arquivos`, `funcionarios`, `chat`, `feed`, and `email`.

## 4. Demo/template routes versus service-backed routes

`src/routes/all_routes.tsx` declares 345 route constants. `src/routes/feature.tsx` declares no route paths; it is the authenticated layout/auth gate that renders `Outlet`, header, sidebar, and global-call UI. The actual registration is in `src/routes/router.link.tsx`: 234 route entries, 233 unique paths (the `/ui-accordion` path is registered twice). Of those unique registered paths, 47 have a service import/prop and 175 public paths have no service binding; the 11 auth paths also have no service binding.

“No service” below means the route element does not import/pass any file from `src/services`. It can still be a shell/demo page, a redirect to a service-backed page, or a page that directly uses Supabase. This is therefore a service-layer audit, not a claim that every no-service page is completely inert.

### Registered public paths with no corresponding service

| Area | Route paths with no service binding | Finding |
|---|---|---|
| Direct/redirect exceptions | `/empresas-grupo`; `/rh/cargos`; `/rh/departamentos`; `/rh/centros-custo` | `/empresas-grupo` queries `empresas_grupo` directly from the page instead of using `empresasGrupoService`. The three RH paths only redirect to tabs in service-backed `/rh/estrutura`. |
| Dashboard/CRM template surfaces | `/dashboard/deals-dashboard`; `/dashboard/leads-dashboard`; `/crm/contact-grid`; `/crm/contact-list`; `/crm/contact-details`; `/crm/companies-grid`; `/crm/companies-list`; `/crm/companies-details`; `/crm/deals`; `/crm/deals-list`; `/crm/deals-details`; `/leads`; `/leads-list`; `/leads-details`; `/crm/pipeline`; `/crm/campaign`; `/crm/campaign-complete`; `/crm/campaign-archieve`; `/crm/tasks-important`; `/crm/tasks-completed`; `/crm/proposals-grid`; `/crm/proposals-list`; `/crm/contracts-grid`; `/crm/contracts-list`; `/crm/estimation-kanban`; `/crm/estimation-list`; `/crm/invoice-grid`; `/crm/invoice-list`; `/crm/payments`; `/crm/analytics`; `/crm/activities` | These route elements are template/demo CRM pages with no service import. The real service-backed work-center paths are separate: `/crm/projects`, `/crm/project-list`, `/crm/project-details`, `/crm/tasks`, and `/support/tickets`. |
| UI/component demos | `/ui-accordion`; `/ui-alerts`; `/ui-avatar`; `/ui-badges`; `/ui-breadcrumb`; `/ui-buttons`; `/ui-buttons-group`; `/ui-cards`; `/ui-carousel`; `/ui-collapse`; `/ui-dropdowns`; `/ui-grid`; `/ui-ratio`; `/ui-image`; `/ui-links`; `/ui-list-group`; `/ui-modals`; `/ui-offcanvas`; `/ui-pagination`; `/ui-placeholder`; `/ui-popover`; `/ui-progress`; `/ui-spinner`; `/ui-navtabs`; `/ui-toasts`; `/ui-tooltip`; `/ui-typography`; `/ui-utilities`; `/ui-drag-drop`; `/ui-clipboard`; `/ui-rangeslider`; `/ui-lightbox`; `/ui-rating`; `/ui-scrollbar`; `/ui-apexchat`; `/icon-bootstrap`; `/ui-flag-icon`; `/ui-fantawesome`; `/icon-ionic`; `/ui-material-icon`; `/ui-icon-pe7`; `/icon-remix`; `/icon-tabler`; `/ui-themify`; `/ui-typicon`; `/ui-weather-icon` | Pure presentation/demo pages; no application service. `/ui-accordion` is duplicated in the route array. |
| Form/table/map demos | `/forms-basic-input`; `/form-checkbox-radios`; `/form-input-groups`; `/form-grid-gutters`; `/form-mask`; `/form-fileupload`; `/form-horizontal`; `/form-vertical`; `/form-floating-labels`; `/form-validation`; `/form-wizard`; `/form-pickers`; `/tables-basic`; `/data-tables`; `/maps-leaflet`; `/form-select`; `/ui-alert` | Pure template controls, map, and alert demonstrations. |
| Application/template pages | `/application/video-call`; `/application/audio-call`; `/application/email-reply`; `/application/todo-List`; `/application/invoice`; `/application/add-invoice`; `/application/edit-invoice`; `/application/invoice-details`; `/blank-page` | These pages are shipped route elements without a matching service. The working email list, chat, calls history, notes, file manager, social feed, and Kanban paths use services where listed above. |
| Reports | `/reports/lead-reports`; `/reports/deal-reports`; `/reports/contact-reports`; `/reports/company-reports`; `/reports/task-reports` | Static/template report pages. `/reports/project-reports` is the service-backed exception. |
| CRM settings/activity templates | `/crm-setting/sources`; `/crm-setting/lost-reason`; `/crm/activity-calls`; `/crm/activity-mail`; `/crm/activity-task`; `/crm/activity-meeting`; `/crm-setting/contact-stage`; `/crm-setting/industry`; `/crm-setting/calls` | Template CRM settings/activity pages; they do not use the similarly named domain services. |
| User/content/support templates | `/user-management/roles-permissions`; `/user-management/permissions`; `/content/pages`; `/content/add-pages`; `/content/edit-pages`; `/content/blogs`; `/content/add-blogs`; `/content/edit-blogs`; `/content/blog-details`; `/content/blog-categories`; `/content/blog-comments`; `/content/blog-tags`; `/content/countries`; `/content/states`; `/content/cities`; `/content/testimonials`; `/content/faq`; `/support/contact-messages`; `/support/tickets-details` | Template CMS, user-management, and support pages. `/support/tickets` is the service-backed exception. |
| General/app/system settings | `/general-settings/profile-settings`; `/general-settings/security`; `/general-settings/notification`; `/general-settings/connected-apps`; `/website-settings/company-settings`; `/website-settings/localization`; `/website-settings/prefixes`; `/website-settings/preference`; `/website-settings/appearance`; `/website-settings/language-web`; `/app-settings/invoice-settings`; `/app-settings/printers`; `/app-settings/custom-fields`; `/system-settings/storage`; `/system-settings/sms-gateways`; `/system-settings/gdpr-cookies` | Settings templates. `/system-settings/storage` renders an email-settings page but does not import `emailService`. |
| Layout variants | `/layout-mini`; `/layout-hoverview`; `/layout-hidden`; `/layout-fullwidth`; `/layout-rtl`; `/layout-dark` | All reuse the template `DelasDashboard` component without a service. |
| Financial/other settings | `/financial-settings/payment-gateways`; `/financial-settings/bank-ccount`; `/financial-settings/tax-rates`; `/financial-settings/currencies`; `/other-settings/sitemap`; `/other-settings/clear-cache`; `/other-settings/storage`; `/other-settings/cronjob`; `/other-settings/ban-ip-address`; `/other-settings/system-backup`; `/other-settings/database-backup`; `/other-settings/system-update`; `/notifications` | Template/settings pages. `/notifications` does not use the available `notificacoesService`. |

### Auth paths with no domain service

`/login` and `/forgot-password` use Supabase Auth directly; `/register`, `/reset-password`, `/email-verification`, `/two-step-verification`, `/lock-screen`, `/error-404`, `/error-500`, `/coming-soon`, and `/under-maintenance` have no service binding. These are auth/utility pages rather than ERP domain demos.

### `all_routes.tsx` constants not registered by `router.link.tsx`

112 constants are declared in `all_routes.tsx` but are not referenced by the route arrays, so they are not counted above as shipped route entries. They include the following paths; they are route constants only in this checkout, not evidence of an active page:

`/sales-dashboard`, `/executive-dashboard`, `/revenue-summary-dashboard`, `/growth-dashboard`, `/super-admin/dashboard`, `/super-admin/company`, `/super-admin/subscription`, `/super-admin/packages`, `/super-admin/packages-grid`, `/super-admin/domain`, `/super-admin/purchase-transaction`, `/crm/addCampaign`, `/leads-kanban`, `/crm/company-details`, `/crm/compaign-complete`, `/companies`, `/crm/proposals-view`, `/bank-accounts`, `/success`, `/ui-alert`, `/ui-accordion`, `/ui-avatar`, `/ui-badges`, `/ui-border`, `/ui-breadcrums`, `/ui-buttons`, `/ui-button-group`, `/ui-cards`, `/ui-carousel`, `/ui-colors`, `/ui-dropdowns`, `/ui-images`, `/ui-media`, `/ui-modals`, `/ui-video`, `/ui-counter`, `/ui-ribbon`, `/ui-sticky-notes`, `/ui-text-editor`, `/ui-timeline`, `/ui-feather-icon`, `/ui-simpleline`, `/select`, `/chart-c3`, `/chart-morris`, `/chart-flot`, `/chart-peity`, `/ui-borders`, `/ui-color`, `/ui-scrollspy`, `/ui-swiperjs`, `/ui-sortable`, `/maps-vector`, `/website-settings/language`, `/application/todo`, `/user-management/delete-request`, `/user-management/manage-users`, `/membership-plans`, `/membership-addons`, `/membership-transactions`, `/projects`, `/tasks`, `/milestones`, `/timesheets`, `/sales-crm/products`, `/sales-crm/product-details`, `/sales-crm/opportunities-list`, `/sales-crm/sales-target`, `/sales-crm/quotations-list`, `/sales-crm/sales-order-list`, `/marketing/email-campaign`, `/marketing/sms-campaign`, `/marketing/social-campaign`, `/marketing/whatsapp-campaign`, `/marketing/email-marketing`, `/marketing/email-engagement`, `/hrm/attendance`, `/hrm/leave-requests`, `/hrm/holidays`, `/user-management/departments`, `/user-management/teams-list`, `/user-management/invitations-list`, `/user-management/staff-directory-grid`, `/user-management/user-activity-logs`, `/user-management/login-history`, `/user-management/device-management`, `/crm-setting/sales-targets-settings`, `/crm-setting/sales-targets-breakdown-settings`, `/crm-setting/discount-rules-settings`, `/crm-setting/applied-discount-log`, `/reports/lead-funnel-report`, `/reports/lead-aging-report`, `/reports/lead-conversion-time-report`, `/reports/deal-conversion-report`, `/reports/deal-aging-report`, `/reports/lost-deal-analysis-report`, `/reports/pipeline-stage-report`, `/reports/revenue-report`, `/reports/user-activity-report`, `/reports/user-login-report`, `/reports/team-performance-report`, `/reports/sales-rep-comparison-report`, `/reports/proposal-report`, `/reports/proposal-conversion-rate-report`, `/reports/contract-report`, `/reports/contract-renewal-expiry-report`, `/reports/estimation-report`, `/reports/attendance-summary-report`, `/reports/leave-balance-summary-report`, `/layout-horizontal-single`, `/layout-without-header`, `/layout-detached`.

## 5. Postgres functions and triggers

The migration scan found 65 `CREATE FUNCTION`/`CREATE OR REPLACE FUNCTION` occurrences representing 59 unique function names, and 31 `CREATE TRIGGER` occurrences representing 28 unique trigger names. Repeated names are redefinitions by later migrations, notably `meus_acessos`, the email permission functions, and the demand trigger functions.

### Functions created or replaced by migrations

| Migration | Function | One-line purpose |
|---|---|---|
| `0005_clientes_codigo.sql` | `gerar_codigo_cliente()` | Assigns the next per-company customer code under a transaction advisory lock when `NEW.codigo` is null. |
| `0008_clientes_pf.sql` | `tocar_atualizado_em()` | Sets `NEW.atualizado_em` to the current time for update triggers. |
| `0016_rbac.sql` | `meu_cargo()` | Returns the current user's `cargo_id` from `perfis`. |
| `0016_rbac.sql` | `ve_todas_empresas()` | Returns whether the current user is an admin or has an active role granting all-company visibility. |
| `0016_rbac.sql` | `ve_consolidado()` | Returns whether the current user is an admin or has an active role granting consolidated visibility. |
| `0016_rbac.sql` | `pode_ver_modulo(p_modulo text)` | Resolves module view permission from admin status, user override, or cargo/module grant. |
| `0016_rbac.sql` | `pode_editar_modulo(p_modulo text)` | Resolves module edit permission from admin status, user override, or cargo/module grant. |
| `0016_rbac.sql` | `tem_acesso_empresa(p_empresa uuid)` | Returns true for global users or users linked to the requested company. |
| `0016_rbac.sql` | `meus_acessos()` | Returns the initial JSON authorization snapshot containing admin/global/consolidated/cargo/module access. |
| `0018_rbac_4acoes.sql` | `pode_criar_modulo(p_modulo text)` | Resolves module create permission using the four-action RBAC model. |
| `0018_rbac_4acoes.sql` | `pode_excluir_modulo(p_modulo text)` | Resolves module delete permission using the four-action RBAC model. |
| `0018_rbac_4acoes.sql` | `meus_acessos()` | Replaces the earlier snapshot with `criar`, `editar`, `ver`, and `excluir` action flags. |
| `0020_funcionarios.sql` | `gerar_matricula_funcionario()` | Assigns the next employee matricula under a transaction advisory lock when absent. |
| `0029_work_center_tickets_tarefas.sql` | `sla_horas(p_prioridade text)` | Maps ticket/task priority to the default SLA duration in hours. |
| `0029_work_center_tickets_tarefas.sql` | `ticket_before_insert()` | Generates a ticket number and fills open time and SLA deadline before insert. |
| `0029_work_center_tickets_tarefas.sql` | `ticket_before_update()` | Updates ticket timestamps/status stamps and recalculates SLA after priority changes. |
| `0029_work_center_tickets_tarefas.sql` | `ticket_after()` | Writes ticket history and queues email/WhatsApp notifications for creation, assignment, and status changes. |
| `0029_work_center_tickets_tarefas.sql` | `tarefa_before_insert()` | Generates a task number and fills SLA/deadline before insert. |
| `0029_work_center_tickets_tarefas.sql` | `tarefa_before_update()` | Updates task timestamps and started/completed stamps as status changes. |
| `0029_work_center_tickets_tarefas.sql` | `tarefa_after()` | Writes task history and queues assignment notifications. |
| `0029_work_center_tickets_tarefas.sql` | `colegas_empresa(p_empresa uuid)` | Returns active profiles visible to a company through membership, all-company cargo, or admin access. |
| `0030_agenda_eventos_feriados.sql` | `garantir_feriados(p_dados jsonb)` | Inserts a supplied holiday JSON array idempotently and returns the processed count. |
| `0031_projetos.sql` | `projeto_before_insert()` | Generates a project number and initializes started/completed timestamps and 100% progress where applicable. |
| `0031_projetos.sql` | `projeto_before_update()` | Updates project timestamps and status-derived started/completed/progress fields. |
| `0031_projetos.sql` | `projeto_after()` | Writes project history and queues assignment notifications for the responsible user. |
| `0031_projetos.sql` | `recalcular_progresso_projeto(p_projeto uuid)` | Recomputes project progress from completed tasks unless the project is completed/cancelled. |
| `0031_projetos.sql` | `tarefa_progresso_trigger()` | Recalculates the affected old/new project when a task is inserted, updated, moved, or deleted. |
| `0033_demandas.sql` | `sla_demanda_horas(p_prioridade text)` | Maps demand priority to the default demand SLA duration in hours. |
| `0033_demandas.sql` | `demanda_before_insert()` | Generates a demand number and initial SLA deadline. |
| `0033_demandas.sql` | `demanda_before_update()` | Updates demand timestamp/completion fields and recalculates SLA after priority changes. |
| `0033_demandas.sql` | `demanda_after()` | Writes basic demand history for creation, status changes, and responsible-user changes. |
| `0037_funcionarios_status_automatico.sql` | `func_status_automatico()` | Derives employee `ativo` from whether `data_demissao` is null. |
| `0043_chat_fase1.sql` | `chat_participa(p_conversa uuid)` | Checks whether the current user participates in a conversation. |
| `0043_chat_fase1.sql` | `chat_toca_conversa()` | Touches a conversation's `atualizado_em` timestamp when a message is inserted. |
| `0043_chat_fase1.sql` | `chat_participa_msg(p_msg uuid)` | Checks message access by resolving its parent conversation participation. |
| `0045_email_fase0.sql` | `mail_pode_ver_conta(p_conta uuid)` | Checks company access plus ownership, explicit permission, or admin status for reading a mailbox. |
| `0045_email_fase0.sql` | `mail_pode_gerenciar_conta(p_conta uuid)` | Checks company access plus ownership, manage permission, or admin status for mailbox management. |
| `0045_email_fase0.sql` | `mail_store_secret(p_conta uuid, p_password text)` | Stores or updates a mailbox password in Supabase Vault and records its secret id. |
| `0045_email_fase0.sql` | `mail_get_secret(p_conta uuid)` | Reads the decrypted Vault secret associated with a mailbox. |
| `0052_email_do_usuario.sql` | `mail_pode_ver_conta(p_conta uuid)` | Replaces the email read check with owner, account permission, or admin checks for user-scoped access. |
| `0052_email_do_usuario.sql` | `mail_pode_gerenciar_conta(p_conta uuid)` | Replaces the email manage check with owner, manage permission, or admin checks. |
| `0054_projetos_dono_membros.sql` | `projeto_sou_dono(p_projeto uuid)` | Checks whether the current user created, owns responsibility for, or sponsors a project. |
| `0054_projetos_dono_membros.sql` | `projeto_sou_membro(p_projeto uuid)` | Checks whether the current user is a member of a project. |
| `0055_kanban_automatizado.sql` | `demanda_sou_dono(p_demanda uuid)` | Checks whether the current user is creator, responsible, or requester for a demand. |
| `0055_kanban_automatizado.sql` | `demanda_sou_envolvido(p_demanda uuid)` | Checks whether the current user is listed in `demanda_envolvidos`. |
| `0055_kanban_automatizado.sql` | `usuario_e_diretor()` | Detects an active user whose active cargo is named Director/Diretora or begins with that title. |
| `0055_kanban_automatizado.sql` | `demanda_pode_trocar_responsavel(p_demanda uuid)` | Allows responsible-user changes for directors, demand creators, or supervisors. |
| `0055_kanban_automatizado.sql` | `demanda_rotina_posso_gerir(p_rotina uuid)` | Allows an admin or routine creator to manage a demand routine. |
| `0055_kanban_automatizado.sql` | `demanda_definir_prazo_etapa(p_rotina uuid, p_status text, p_inicio timestamptz)` | Computes a stage deadline from the configured routine stage's `prazo_horas`. |
| `0055_kanban_automatizado.sql` | `demanda_proximo_status(p_rotina uuid, p_status text)` | Selects the next configured stage by order, with a fallback sequence when no routine exists. |
| `0055_kanban_automatizado.sql` | `demanda_gatilho_etapa(p_rotina uuid, p_status text)` | Returns the configured completion trigger for a stage, defaulting to `conclusao`. |
| `0055_kanban_automatizado.sql` | `demanda_before_insert()` | Routes a new demand to the most specific routine, assigns its responsible user, initializes stage/checklist/deadlines, and normalizes the automated workflow state. |
| `0055_kanban_automatizado.sql` | `demanda_before_update()` | Applies operational normalization, detects stage/checklist/time transitions, recalculates deadlines, and resets/advances automation state. |
| `0055_kanban_automatizado.sql` | `concluir_etapa_demanda(p_demanda uuid)` | Locks a demand and advances it when the configured completion trigger is satisfied. |
| `0055_kanban_automatizado.sql` | `retomar_demanda_atrasada(p_demanda uuid, p_novo_prazo timestamptz)` | Validates a future deadline and reopens a demand previously moved to the delayed stage. |
| `0055_kanban_automatizado.sql` | `notificar_envolvidos_demanda(p_demanda uuid, p_evento text, p_titulo text, p_mensagem text, p_chave text)` | Fans a demand event out to responsible/requester/creator/involved users and the external notification queue. |
| `0055_kanban_automatizado.sql` | `demanda_envolvido_after_insert()` | Notifies a newly added demand participant and records the corresponding system notification. |
| `0055_kanban_automatizado.sql` | `demanda_after()` | Writes demand history and emits notifications after creation, stage/status, responsible, or delay changes. |
| `0055_kanban_automatizado.sql` | `processar_automacoes_demandas()` | Batch-processes time-triggered and overdue demands, advances eligible stages, marks delays, and reports automation counts. |
| `0057_email_correcao_integral.sql` | `mail_pode_enviar_conta(p_conta uuid)` | Checks whether the current user may enqueue/send mail for a mailbox. |
| `0057_email_correcao_integral.sql` | `mail_pode_excluir_conta(p_conta uuid)` | Checks whether the current user may delete a mailbox. |
| `0057_email_correcao_integral.sql` | `mail_claim_sync(p_conta uuid, p_token uuid)` | Atomically claims a mailbox synchronization run and returns the claimed account state/row count. |
| `0057_email_correcao_integral.sql` | `mail_storage_conta_id(p_name text)` | Extracts the leading mailbox UUID from an email storage object path for bucket authorization. |
| `0058_usuario_colaborador_unico.sql` | `validar_vinculo_usuario_colaborador()` | Rejects a funcionario/profile link when the selected profile does not point back to that employee. |
| `0058_usuario_colaborador_unico.sql` | `criar_perfil_novo_usuario()` | Creates a profile for a new Auth user and makes the first profile an admin. |

### Functions and trigger in `schema.sql`

These are outside the migration-only count above but are also present in the requested `schema.sql` source:

| Source | Function/trigger | One-line purpose |
|---|---|---|
| `schema.sql` | `eh_admin()` | Returns whether the current authenticated profile is active and has `papel = 'admin'`. |
| `schema.sql` | `tem_acesso_empresa(p_empresa uuid)` | Returns whether the current user is admin or has a `perfil_empresas` link to the company. |
| `schema.sql` | `criar_perfil_novo_usuario()` | Auth-user trigger function that inserts a profile and promotes the first profile to admin. |
| `schema.sql` | `trg_criar_perfil` | Runs `criar_perfil_novo_usuario()` after each insert into `auth.users`. |

### Triggers created by migrations

| Migration | Trigger | Event/table and one-line purpose |
|---|---|---|
| `0005_clientes_codigo.sql` | `trg_codigo_cliente` | `BEFORE INSERT` on `clientes`; invokes per-company customer-code generation. |
| `0008_clientes_pf.sql` | `trg_clientes_atualizado_em` | `BEFORE UPDATE` on `clientes`; touches `atualizado_em`. |
| `0020_funcionarios.sql` | `trg_matricula_funcionario` | `BEFORE INSERT` on `funcionarios`; generates the employee matricula. |
| `0025_produtividade_tarefas_notas.sql` | `trg_tarefas_atualizado_em` | `BEFORE UPDATE` on `tarefas`; touches the update timestamp. |
| `0025_produtividade_tarefas_notas.sql` | `trg_notas_atualizado_em` | `BEFORE UPDATE` on `notas`; touches the update timestamp. |
| `0026_produtividade_arquivos.sql` | `trg_arquivos_atualizado_em` | `BEFORE UPDATE` on `arquivos`; touches the update timestamp. |
| `0027_produtividade_kanban.sql` | `trg_kanban_cartoes_atualizado_em` | `BEFORE UPDATE` on `kanban_cartoes`; touches the update timestamp. |
| `0029_work_center_tickets_tarefas.sql` | `trg_ticket_bi` | `BEFORE INSERT` on `tickets`; assigns number/open time/SLA. |
| `0029_work_center_tickets_tarefas.sql` | `trg_ticket_bu` | `BEFORE UPDATE` on `tickets`; stamps status/time changes and recalculates SLA. |
| `0029_work_center_tickets_tarefas.sql` | `trg_ticket_af` | `AFTER INSERT OR UPDATE` on `tickets`; records history and notification queue entries. |
| `0029_work_center_tickets_tarefas.sql` | `trg_tarefa_bi` | `BEFORE INSERT` on `tarefas`; assigns number/SLA/deadline. |
| `0029_work_center_tickets_tarefas.sql` | `trg_tarefa_bu` | `BEFORE UPDATE` on `tarefas`; stamps start/completion state and update time. |
| `0029_work_center_tickets_tarefas.sql` | `trg_tarefa_af` | `AFTER INSERT OR UPDATE` on `tarefas`; records history and assignment notifications. |
| `0030_agenda_eventos_feriados.sql` | `trg_eventos_atualizado_em` | `BEFORE UPDATE` on `eventos`; touches the update timestamp. |
| `0031_projetos.sql` | `trg_projeto_bi` | `BEFORE INSERT` on `projetos`; assigns number and initial status-derived fields. |
| `0031_projetos.sql` | `trg_projeto_bu` | `BEFORE UPDATE` on `projetos`; maintains status timestamps/progress. |
| `0031_projetos.sql` | `trg_projeto_af` | `AFTER INSERT OR UPDATE` on `projetos`; records history and manager notifications. |
| `0031_projetos.sql` | `trg_tarefa_progresso` | `AFTER INSERT OR UPDATE OR DELETE` on `tarefas`; recalculates linked project progress. |
| `0033_demandas.sql` | `trg_demanda_bi` | `BEFORE INSERT` on `demandas`; assigns number and basic SLA. |
| `0033_demandas.sql` | `trg_demanda_bu` | `BEFORE UPDATE` on `demandas`; maintains timestamp/completion/SLA fields. |
| `0033_demandas.sql` | `trg_demanda_af` | `AFTER INSERT OR UPDATE` on `demandas`; records basic demand history. |
| `0037_funcionarios_status_automatico.sql` | `trg_func_status_automatico` | `BEFORE INSERT OR UPDATE` on `funcionarios`; derives active status from termination date. |
| `0040_feed_corporativo.sql` | `trg_feed_canais_atualizado` | `BEFORE UPDATE` on `feed_canais`; touches the update timestamp. |
| `0040_feed_corporativo.sql` | `trg_feed_pub_atualizado` | `BEFORE UPDATE` on `feed_publicacoes`; touches the update timestamp. |
| `0043_chat_fase1.sql` | `trg_chat_conversas_atualizado` | `BEFORE UPDATE` on `chat_conversas`; touches the update timestamp. |
| `0043_chat_fase1.sql` | `trg_chat_msg_toca_conversa` | `AFTER INSERT` on `chat_mensagens`; touches the parent conversation timestamp. |
| `0055_kanban_automatizado.sql` | `trg_demanda_envolvido_ai` | `AFTER INSERT` on `demanda_envolvidos`; notifies the newly added participant. |
| `0055_kanban_automatizado.sql` | `trg_demanda_bi` | Replaces the earlier `demandas` `BEFORE INSERT` trigger with routine routing, responsible assignment, checklist, stage, and deadline initialization. |
| `0055_kanban_automatizado.sql` | `trg_demanda_bu` | Replaces the earlier `demandas` `BEFORE UPDATE` trigger with automated stage/checklist/time transition logic. |
| `0055_kanban_automatizado.sql` | `trg_demanda_af` | Replaces the earlier `demandas` `AFTER INSERT OR UPDATE` trigger with history plus system/external notifications. |
| `0058_usuario_colaborador_unico.sql` | `trg_validar_vinculo_usuario_colaborador` | `BEFORE INSERT OR UPDATE OF perfil_id` on `funcionarios`; enforces the bidirectional employee/profile link. |

The central Kanban-automation path is therefore: demand insert/update triggers normalize and route the demand; `demanda_rotinas` and `demanda_rotina_etapas` supply the workflow; `concluir_etapa_demanda` advances on configured completion/checklist/time rules; `processar_automacoes_demandas` handles batch time/overdue transitions; and the after/participant triggers write `work_historico`, `notificacoes_usuario`, and the external notification queue.
