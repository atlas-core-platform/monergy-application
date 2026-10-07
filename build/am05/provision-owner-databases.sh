#!/usr/bin/env bash
set -euo pipefail
container_id="$1"
for service in CI AUDIT; do
  for tenant in T001 T002; do
    variable="AM05_${service}_${tenant}_OWNER"
    export AM05_MIGRATION_CONNECTION="${!variable}"
    service_id="audit"
    if [ "$service" = CI ]; then service_id="customer-identity"; fi
    dotnet build/Monergy.DatabaseMigrator/bin/Release/net10.0/Monergy.DatabaseMigrator.dll \
      --service "$service_id" --connection-environment AM05_MIGRATION_CONNECTION --repository-root "$PWD"
    database="am05_${service,,}_${tenant,,}"
    runtime="${database}_runtime"
    if [ "$service" = CI ]; then
      docker exec -i "$container_id" psql -U postgres -d "$database" -v ON_ERROR_STOP=1 --set=tenant="$tenant" --set=runtime="$runtime" <<'SQL'
INSERT INTO customer_identity.tenant_identity VALUES (true,:'tenant',1);
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA customer_identity TO %I', :'runtime') \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA customer_identity TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT(session_hash,actor_id,subject_version),UPDATE(revoked) ON customer_identity.trusted_sessions TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT,UPDATE(version) ON customer_identity.access_subject_versions TO %I', :'runtime') \gexec
SELECT format('GRANT UPDATE(version) ON customer_identity.access_policy_version TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT ON customer_identity.access_event_inbox TO %I', :'runtime') \gexec
SQL
    else
      docker exec -i "$container_id" psql -U postgres -d "$database" -v ON_ERROR_STOP=1 --set=tenant="$tenant" --set=runtime="$runtime" <<'SQL'
INSERT INTO audit.access_tenant_identity VALUES (true,:'tenant',1);
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA audit TO %I', :'runtime') \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA audit TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT ON audit.evidence,audit.inbox,audit.access_event_inbox TO %I', :'runtime') \gexec
SQL
    fi
  done
done
