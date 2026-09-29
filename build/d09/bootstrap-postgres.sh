#!/usr/bin/env bash
set -euo pipefail

services=(evidence financial_profile financial_rules reporting audit)
for service in "${services[@]}"; do
  upper="${service^^}"
  owner="monergy_${service}_owner"
  runtime="monergy_${service}_runtime"
  database="monergy_${service}"
  owner_password_var="MONERGY_${upper}_OWNER_PASSWORD"
  runtime_password_var="MONERGY_${upper}_RUNTIME_PASSWORD"
  owner_password="${!owner_password_var}"
  runtime_password="${!runtime_password_var}"

  psql --username "$POSTGRES_USER" --dbname postgres \
    --set=owner="$owner" --set=runtime="$runtime" \
    --set=owner_password="$owner_password" --set=runtime_password="$runtime_password" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'owner', :'owner_password') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'runtime', :'runtime_password') \gexec
SQL
  psql --username "$POSTGRES_USER" --dbname postgres --set=database="$database" --set=owner="$owner" <<'SQL'
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'owner') \gexec
SQL
  psql --username "$POSTGRES_USER" --dbname postgres --set=database="$database" --set=owner="$owner" --set=runtime="$runtime" <<'SQL'
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', :'database') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'owner') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'runtime') \gexec
SQL
done
