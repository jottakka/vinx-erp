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

Saída recusada dentro do repositório; diretório criado 0700; qualquer falha
descarta o bundle parcial (nada de capturas mistas).

Segredos nunca vão para argv: a connection string chega só por ambiente e vira
um `pgpass` temporário 0600; chaves de serviço viram arquivos de cabeçalho 0600;
ambos são removidos ao sair (`lib-db.sh`).

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
