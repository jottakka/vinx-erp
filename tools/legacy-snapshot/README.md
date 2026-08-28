# legacy-snapshot

Captura determinística e somente-leitura do estate Supabase legado. Roda com o
papel Postgres de leitura fornecido pelo responsável; nenhum dado sai da
infraestrutura do cliente além do bundle local definido por ele.

## snapshot.sh — bundle versionado

```bash
LEGACY_SNAPSHOT_CONN='postgresql://leitura:***@host:5432/postgres' \
  tools/legacy-snapshot/snapshot.sh /destino/fora-do-repositorio/bundle-1
```

Gera `sql/schema.sql` (pg_dump --schema-only único), `json/*.json`
(server, extensions, roles, grants, sequences, policies, cron_jobs,
row_counts, storage), `commands/auth-export.*` (executa o responsável) e
`manifest.sha256` (payload ordenado). Metadados ficam em
`bundle_metadata.json`, fora do hash.

Saída recusada dentro da árvore do repositório (raiz resolvida a partir do
diretório do script, não do cwd de quem chama); a construção ocorre em
diretório irmão privado 0700 com nome mktemp+PID (posse inequívoca). A
publicação REIVINDICA o destino com `mkdir` atômico e marca a posse com PID:
dois publicadores concorrente jamais aninham ou sobrescrevem caminho alheio, e
a limpeza de erro/INT/TERM/HUP remove apenas caminhos criados pelo próprio
processo — bundle parcial nunca sobrevive, bundle concorrente nunca é tocado.
Inventário de Storage é parseado estruturalmente (JSON), paginado por nível e
RECURSIVO pelos prefixos de pasta (a listagem da API é hierárquica: sem
travessia, objetos abaixo de pastas ficariam fora do manifesto), com tetos de
segurança de profundidade/pastas/objetos.

## tests/ — contrato executável do shell

`tests/run-tests.sh` executa o `snapshot.sh` real com bins `psql`/`pg_dump`
falsos e um mock local da Storage API (python3 `http.server`): guarda de
destino dentro do repositório a partir de cwd estranho, inventário hierárquico
com paginação múltipla, aborto por JSON de buckets malformado, publicação
concorrente (perdedor falha limpo, vencedor íntegro), limpeza em INT/TERM e
teardown do bearer. Sem Postgres, sem Docker, sem rede externa.

Segredos nunca vão para argv: a connection string chega só por ambiente, vira
um `pgpass` temporário 0600 e valores percent-codificados são decodificados
por python3 lendo stdin; controles TLS explícitos (`sslmode`, `sslrootcert`)
são preservados e repassados ao libpq via `PGSSLMODE`/`PGSSLROOTCERT`; chaves
de serviço viram arquivos de cabeçalho 0600 registrados no shell pai para o
teardown (`lib-db.sh`).

## profile-tables.sh — agregados para as ledgers

```bash
cat <<'EOF' > /tmp/perfil.config
demandas.status
tickets.status
EOF

LEGACY_SNAPSHOT_CONN='postgresql://leitura:***@host:5432/postgres' \
  tools/legacy-snapshot/profile-tables.sh /tmp/perfil.config /destino/perfil-1
```

Somente agregações: contagens/tamanhos, taxa de nulos, TOP 100 valores
distintos por coluna configurada (alimenta a ledger de status) e min/max de
datas. A SQL gerada é gravada junto à saída para auditoria.

Nota: o importador lê o schema declarado em `sourceSchema` (padrão `legacy`);
para ensaio em espelho com nome diferente, renomeie ou declare o campo.
