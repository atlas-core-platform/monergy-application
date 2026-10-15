#!/usr/bin/env bash
set -euo pipefail
container_id="$1"
for tenant in T001 T002; do
  lower="${tenant,,}"
  owner="am_${lower}_owner"
  runtime="am_${lower}_runtime"
  delivery="am_${lower}_delivery"
  database="am_${lower}"
  owner_password="$(openssl rand -hex 24)"
  runtime_password="$(openssl rand -hex 24)"
  delivery_password="$(openssl rand -hex 24)"
  echo "::add-mask::$owner_password"
  echo "::add-mask::$runtime_password"
  echo "::add-mask::$delivery_password"
  docker exec -i "$container_id" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    --set=owner="$owner" --set=runtime="$runtime" --set=database="$database" \
    --set=owner_password="$owner_password" --set=runtime_password="$runtime_password" --set=delivery="$delivery" --set=delivery_password="$delivery_password" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'owner', :'owner_password') \gexec
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'runtime', :'runtime_password') \gexec
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'delivery', :'delivery_password') \gexec
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'owner') \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', :'database') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'runtime') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'database', :'delivery') \gexec
SQL
  printf 'MONERGY_AM_%s_OWNER=Host=127.0.0.1;Port=55432;Database=%s;Username=%s;Password=%s;Timeout=5\n' "$tenant" "$database" "$owner" "$owner_password" >> "$GITHUB_ENV"
  printf 'MONERGY_AM_%s_RUNTIME=Host=127.0.0.1;Port=55432;Database=%s;Username=%s;Password=%s;Timeout=5\n' "$tenant" "$database" "$runtime" "$runtime_password" >> "$GITHUB_ENV"
  printf 'MONERGY_AM_%s_DELIVERY=Host=127.0.0.1;Port=55432;Database=%s;Username=%s;Password=%s;Timeout=5\n' "$tenant" "$database" "$delivery" "$delivery_password" >> "$GITHUB_ENV"
done
receiver_owner_password="$(openssl rand -hex 24)"
receiver_runtime_password="$(openssl rand -hex 24)"
transport_token="$(openssl rand -hex 32)"
echo "::add-mask::$receiver_owner_password"
echo "::add-mask::$receiver_runtime_password"
echo "::add-mask::$transport_token"
docker exec -i "$container_id" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
  --set=owner_password="$receiver_owner_password" --set=runtime_password="$receiver_runtime_password" <<'SQL'
SELECT format('CREATE ROLE am_delivery_receiver_owner LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'owner_password') \gexec
SELECT format('CREATE ROLE am_delivery_receiver_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'runtime_password') \gexec
CREATE DATABASE am_delivery_receiver OWNER am_delivery_receiver_owner;
REVOKE ALL ON DATABASE am_delivery_receiver FROM PUBLIC;
GRANT CONNECT ON DATABASE am_delivery_receiver TO am_delivery_receiver_runtime;
SQL
printf 'MONERGY_AM_REFERENCE_RECEIVER_OWNER=Host=127.0.0.1;Port=55432;Database=am_delivery_receiver;Username=am_delivery_receiver_owner;Password=%s;Timeout=5\n' "$receiver_owner_password" >> "$GITHUB_ENV"
printf 'MONERGY_AM_REFERENCE_RECEIVER_RUNTIME=Host=127.0.0.1;Port=55432;Database=am_delivery_receiver;Username=am_delivery_receiver_runtime;Password=%s;Timeout=5\n' "$receiver_runtime_password" >> "$GITHUB_ENV"
printf 'MONERGY_AM_REFERENCE_TRANSPORT_TOKEN=%s\n' "$transport_token" >> "$GITHUB_ENV"
