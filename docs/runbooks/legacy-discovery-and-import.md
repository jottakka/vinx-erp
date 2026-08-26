# Runbook — descoberta e importação do legado

Descoberta do estate Supabase do cliente (~102 tabelas) e importação idempotente
para o staging do ERP alvo. Ferramentas: `tools/legacy-snapshot/` e
`src/Tools/Vinx.Erp.LegacyImport/`. Ledgers: `docs/migration/ledgers/`.

## Entradas bloqueadas no responsável (checklist)

Nada é simulado nesta fase; cada item abaixo precisa ser liberado pelo dono:

| # | Entrada | Como chegar |
|---|---|---|
| 1 | Papel Postgres somente-leitura + connection string | Responsável cria papel `SELECT` nos schemas de aplicação; jamais vai para logs/commits |
| 2 | Acesso de export do Auth (`auth.users`, `auth.identities`) | Comandos prontos em `commands/auth-export.*` dentro do bundle |
| 3 | Listagem dos buckets de Storage (metadados) | `LEGACY_SNAPSHOT_STORAGE_URL` + chave de leitura no `snapshot.sh` |
| 4 | Ledger das migrações aplicadas do cliente | Consulta à tabela de histórico do Supabase do cliente, anexada ao bundle pelo responsável |
| 5 | Jobs de pg_cron | Capturado pelo próprio script quando a extensão está exposta |

## Ordem de operações

1. **Snapshot imutável** — rodar `snapshot.sh` uma única vez contra produção com o papel de leitura. Um bundle vale como referência canônica da descoberta.
2. **Profiling agregado** — rodar `profile-tables.sh` (ou capturar os mesmos agregados na mesma sessão do passo 1) ANTES de preencher qualquer ledger à mão: as listas de valores distintos por coluna configurada alimentam `status-vocabulary-mapping.md`.
3. **Readback das ledgers** — preencher célula a célula com evidência (contagem/tabela do snapshot); regra vigente: célula vazia até a leitura de retorno confirmada.
4. **Contrato alvo** do vertical antes de importar qualquer linha dele.
5. **Rehearsal ×2 obrigatório** — importar em ambiente de ensaio duas vezes seguidas; a segunda execução deve reportar replay/skip sem alterar linhas nem receipts. O gate só abre após dois ensaios verdes com logs arquivados pelo responsável.
6. **Corte com congelamento de escrita** — janela curta com escritas do sistema antigo suspensas; novo snapshot delta de conferência (contagens) na janela; então aplicar a importação final com `--apply`.
7. **Validação pós-importação** — comparação de contagens/valores-chave contra o bundle.

## Regras duras

- **Filas externas nunca entram como trabalho ativo**: itens pendentes em
  `mail_outbound_queue` e `notificacoes_fila` são drenados ou descartados no
  sistema antigo; a importação não os recreia como pendências vivas.
- **Nunca dual-write**; CDC só se restrição de downtime forçar decisão futura.
- Célula vazia nas ledgers = bloqueio de importação (fail-closed no importador).
- Arquivos migram em tarefa própria, via quarentena — fora do escopo desta fase.

## Contrato do importador (`Vinx.Erp.LegacyImport`)

- Modo padrão é **dry-run**: valida mapeamentos e colunas sem escrever nada,
  inclusive receipts. Escritas só com `--apply`.
- Escreve apenas no schema `legacy_import`: tabela alvo do vertical
  (`customers`) e `import_receipts` com UNIQUE(company_key, table_key,
  source_batch_id, mapping_version_hash).
- Transação única por empresa: `pg_try_advisory_xact_lock(hashtext(company_key))`;
  quem não obtém o lock registra `concurrent_skip` e sai sem escrever.
- Receipt com `ON CONFLICT DO NOTHING`: inseriu → lote novo; conflito → comparar
  hash de mapeamento + digest do payload: iguais ⇒ replay idempotente (skip);
  diferentes ⇒ exceção e rollback completo daquela empresa (entrada mutada sob o
  mesmo batch é violação fail-closed).

## Postura de segurança dos bundles

O bundle de snapshot contém identidades, políticas e metadados sensíveis:

- Confidencial por padrão; nenhum conteúdo entra em logs, issues, PRs ou commits.
- O destino do bundle é aprovado pelo responsável; o script recusa caminhos
  dentro do repositório e cria o diretório com permissão 0700.
- Retenção e destruição ficam registradas pelo responsável (data, local, quem).
- A connection string usada pelos scripts nunca é impressa nem embarcada no bundle.

## Consistência das capturas

Todas as leituras que embasam as ledgers devem sair de **uma única captura
consistente**: uma transação `REPEATABLE READ READ ONLY` (os scripts já fazem
isso) e uma passada única de `pg_dump`. Capturas parciais misturadas — schema de
um horário, contagens de outro, perfis de um terceiro — invalidam todas as
ledgers derivadas, porque decidem classificação/posse sobre estados que nunca
coexistiram. Se falhar, descartar o bundle parcial (comportamento padrão dos
scripts) e recomeçar.
