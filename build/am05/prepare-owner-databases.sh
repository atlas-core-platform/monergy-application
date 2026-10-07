#!/usr/bin/env bash
set -euo pipefail
container_id="$1"
docker exec -i "$container_id" psql -U postgres -d postgres -v ON_ERROR_STOP=1 <<'SQL'
CREATE ROLE monergy_audit_runtime NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE;
SQL
for service in CI AUDIT; do
  for tenant in T001 T002; do
    database="am05_${service,,}_${tenant,,}"
    owner="${database}_owner"
    runtime="${database}_runtime"
    owner_password="$(openssl rand -hex 24)"
    runtime_password="$(openssl rand -hex 24)"
    echo "::add-mask::$owner_password"
    echo "::add-mask::$runtime_password"
    docker exec -i "$container_id" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
      --set=owner="$owner" --set=runtime="$runtime" --set=database="$database" \
      --set=owner_password="$owner_password" --set=runtime_password="$runtime_password" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'owner', :'owner_password') \gexec
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'runtime', :'runtime_password') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'owner') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'database') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'runtime') \gexec
SQL
    printf 'AM05_%s_%s_OWNER=Host=127.0.0.1;Port=55432;Database=%s;Username=%s;Password=%s;Timeout=5\n' "$service" "$tenant" "$database" "$owner" "$owner_password" >> "$GITHUB_ENV"
    printf 'AM05_%s_%s_RUNTIME=Host=127.0.0.1;Port=55432;Database=%s;Username=%s;Password=%s;Timeout=5\n' "$service" "$tenant" "$database" "$runtime" "$runtime_password" >> "$GITHUB_ENV"
  done
done
for name in MEMBERSHIP CI_CONTEXT CI_EVENT CI_PROVISIONING AUDIT_EVENT T001_A900 T001_A100 T002_A900 T002_A100; do
  token="$(openssl rand -hex 32)"
  echo "::add-mask::$token"
  printf 'AM05_%s_TOKEN=%s\n' "$name" "$token" >> "$GITHUB_ENV"
done
