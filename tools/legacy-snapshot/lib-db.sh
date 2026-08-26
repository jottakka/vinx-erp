#!/usr/bin/env bash
# Biblioteca compartilhada dos scripts de captura legada.
#
# Contrato de segredos: a connection string chega APENAS pela variável de
# ambiente LEGACY_SNAPSHOT_CONN e é convertida em um pgpass temporário 0600
# (PGPASSFILE); psql/pg_dump recebem apenas -h/-p/-U/-d não secretos. Chaves
# de serviço (Storage) são entregues ao curl por arquivo de cabeçalho 0600.
# Falha fechada quando a string não pode ser interpretada com segurança.

# Estado populado por db_setup:
DB_HOST= DB_PORT=5432 DB_USER= DB_NAME= DB_PASS=
PGPASS_FILE=
BEARER_FILES=()

db_fail() { echo "erro(lib-db): $*" >&2; exit 2; }

db_percent_decode() {
  local value=$1
  if command -v python3 >/dev/null 2>&1; then
    python3 -c 'import sys, urllib.parse; sys.stdout.write(urllib.parse.unquote(sys.argv[1]))' "$value"
  elif [[ "$value" != *%* ]]; then
    printf '%s' "$value"
  else
    db_fail "credencial com codificação percentual exige python3 disponível para decodificação segura"
  fi
}

db_kv_value() {
  # Extrai valor de chave em strings tipo "host=x port=y password=z".
  local token
  for token in $1; do
    [[ "$token" == "$2="* ]] && { printf '%s' "${token#"$2="}"; return 0; }
  done
  return 1
}

db_setup() {
  : "${LEGACY_SNAPSHOT_CONN:?LEGACY_SNAPSHOT_CONN é obrigatória}"
  # Reset defensivo: nenhuma credencial sobrevive de uma chamada anterior.
  local conn rest userinfo hostport kv
  DB_HOST= DB_PORT=5432 DB_USER= DB_NAME= DB_PASS=
  conn=$LEGACY_SNAPSHOT_CONN

  case "$conn" in
    postgresql://*|postgres://*)
      rest=${conn#*://}
      [[ "$rest" == */* ]] || db_fail "connection string sem nome de banco"
      userinfo=${rest%%/*}
      rest=${rest#*/}
      kv=${rest%%\?*}
      DB_NAME=$(db_percent_decode "$kv") || exit 2
      [[ -n "$DB_NAME" ]] || db_fail "banco vazio na connection string"
      if [[ "$userinfo" == *@* ]]; then
        hostport=${userinfo##*@}
        DB_USER=${userinfo%%@*}
      else
        hostport=$userinfo
      fi
      if [[ "$DB_USER" == *:* ]]; then
        DB_PASS=${DB_USER#*:}
        DB_USER=${DB_USER%%:*}
      fi
      DB_USER=$(db_percent_decode "${DB_USER:-$(id -un)}") || exit 2
      DB_PASS=$(db_percent_decode "${DB_PASS:-}") || exit 2
      if [[ "$hostport" == "["*"]"* ]]; then
        DB_HOST=${hostport%%]*}
        DB_HOST=${DB_HOST#[}
        DB_PORT=${hostport##*]:}
        [[ "$DB_PORT" != "$hostport" ]] || DB_PORT=5432
      elif [[ "$hostport" == *:* ]]; then
        DB_HOST=${hostport%%:*}
        DB_PORT=${hostport##*:}
      else
        DB_HOST=$hostport
      fi
      ;;
    *\ *)
      local token acc="" benign="sslmode= sslrootcert= connect_timeout= application_name="
      for token in $conn; do
        case "$token" in
          host=*) DB_HOST=${token#host=} ;;
          port=*) DB_PORT=${token#port=} ;;
          user=*) DB_USER=${token#user=} ;;
          dbname=*|database=*) DB_NAME=${token#*=} ;;
          password=*) DB_PASS=${token#password=} ;;
          sslmode=*|sslrootcert=*|connect_timeout=*|application_name=*) : ;; # inofensivos para pgpass; aplicados por padrão do libpq
          *) acc+=" $token" ;;
        esac
      done
      [[ "$acc" == *"="* ]] && db_fail "parâmetros não suportados na connection string:$acc"
      unset benign
      ;;
    *)
      db_fail "formato de connection string não reconhecido (use postgres:// ou pares chave=valor)"
      ;;
  esac

  : "${DB_HOST:=localhost}"
  [[ -n "$DB_PORT" ]] || DB_PORT=5432
  [[ -n "$DB_USER" ]] || DB_USER="$(id -un)"
  [[ -n "$DB_NAME" ]] || db_fail "dbname ausente; informe o banco na connection string"
  [[ -n "$DB_PASS" ]] || db_fail "senha ausente na connection string; papel somente-leitura requer credencial"

  local forbidden=$':\\\n'
  case "$DB_PASS$DB_USER$DB_NAME$DB_HOST" in
    *$forbidden*) db_fail "credenciais contêm caracteres proibidos (dois-pontos, barra invertida, nova linha)" ;;
  esac

  PGPASS_FILE=$(mktemp "${TMPDIR:-/tmp}/vinx-pgpass.XXXXXX")
  chmod 600 "$PGPASS_FILE"
  printf '%s:%s:%s:%s:%s\n' "$DB_HOST" "$DB_PORT" "$DB_NAME" "$DB_USER" "$DB_PASS" > "$PGPASS_FILE"
  export PGPASSFILE="$PGPASS_FILE"
}

db_psql() {
  psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -X -v ON_ERROR_STOP=1 "$@"
}

db_pg_dump() {
  pg_dump -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" "$@"
}

db_bearer_header_file() {
  local file
  file=$(mktemp "${TMPDIR:-/tmp}/vinx-hdr.XXXXXX")
  chmod 600 "$file"
  printf 'Authorization: Bearer %s\n' "$1" > "$file"
  BEARER_FILES+=("$file")
  printf '%s' "$file"
}

db_teardown_secrets() {
  [[ -n "${PGPASS_FILE:-}" ]] && rm -f "$PGPASS_FILE"
  local f
  for f in ${BEARER_FILES[@]+"${BEARER_FILES[@]}"}; do
    rm -f "$f"
  done
}
