#!/usr/bin/env bash
set -euo pipefail
container_id="$1"
export ASPNETCORE_ENVIRONMENT=Development
export MONERGY_AM_REFERENCE_PROVISIONING=1
tool="tools/Monergy.AccessManagement.Migrations/bin/Release/net10.0/Monergy.AccessManagement.Migrations.dll"
for tenant in T001 T002; do
  owner_variable="MONERGY_AM_${tenant}_OWNER"
  export MONERGY_AM_MIGRATION_CONNECTION="${!owner_variable}"
  dotnet "$tool" "$tenant" A900
  lower="${tenant,,}"
  docker exec -i "$container_id" psql -U postgres -d "am_${lower}" -v ON_ERROR_STOP=1 --set=runtime="am_${lower}_runtime" --set=delivery="am_${lower}_delivery" <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA am TO %I', :'runtime') \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA am TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT,UPDATE,DELETE ON am.permissions,am.roles,am.permission_capabilities,am.role_permissions,am.access_subjects,am.resource_grants,am.customer_relationships,am.groups,am.group_members,am.import_batches,am.import_rows,am.import_email_reservations TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT ON am.default_access_pack_history TO %I', :'runtime') \gexec
SELECT format('GRANT UPDATE (default_access_pack_version,default_access_pack_installed_at) ON am.tenant_identity TO %I', :'runtime') \gexec
SELECT format('GRANT UPDATE (policy_version) ON am.policy_state TO %I', :'runtime') \gexec
SELECT format('GRANT INSERT ON am.outbox TO %I', :'runtime') \gexec
SELECT format('GRANT USAGE ON SCHEMA am TO %I', :'delivery') \gexec
SELECT format('GRANT SELECT ON am.tenant_identity,am.policy_state,am.outbox,am.outbox_delivery,am.outbox_redrives TO %I', :'delivery') \gexec
SELECT format('GRANT INSERT,UPDATE ON am.outbox_delivery TO %I', :'delivery') \gexec
SELECT format('GRANT INSERT ON am.outbox_redrives TO %I', :'delivery') \gexec
SQL
done
for tenant in T001 T002; do
  lower="${tenant,,}"
  docker exec -i "$container_id" psql -U postgres -d "am_${lower}" -v ON_ERROR_STOP=1 -At <<'SQL' | grep -qx '1|8|13|7'
SELECT
  CASE WHEN (SELECT default_access_pack_version FROM am.tenant_identity WHERE singleton)='2.0' THEN 1 ELSE 0 END,
  (SELECT count(*) FROM am.roles),
  (SELECT count(*) FROM am.permissions),
  (SELECT count(*) FROM am.roles r WHERE NOT EXISTS (
      SELECT 1 FROM am.role_permissions rp WHERE rp.role_id=r.role_id));
SQL
done
export MONERGY_AM_MIGRATION_CONNECTION="$MONERGY_AM_T001_OWNER"
dotnet "$tool" T001 A900
if dotnet "$tool" T001 A901; then
  echo "ERROR: second initial administrator was accepted" >&2
  exit 1
fi
if dotnet "$tool" T002 A900; then
  echo "ERROR: mismatched tenant/database binding was accepted" >&2
  exit 1
fi
echo "Migration replay, bootstrap uniqueness, default access pack, and tenant binding checks passed."

{ printf 'SET ROLE am_delivery_receiver_owner;\n'; cat tools/Monergy.AccessManagement.DeliveryReference/fixture.sql; } | docker exec -i "$container_id" psql -U postgres -d am_delivery_receiver -v ON_ERROR_STOP=1
docker exec -i "$container_id" psql -U postgres -d am_delivery_receiver -v ON_ERROR_STOP=1 <<'SQL'
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA delivery_reference TO am_delivery_receiver_runtime;
GRANT SELECT ON ALL TABLES IN SCHEMA delivery_reference TO am_delivery_receiver_runtime;
GRANT INSERT ON delivery_reference.inbox,delivery_reference.audit_observations,delivery_reference.policy_versions,delivery_reference.subject_versions,delivery_reference.session_versions,delivery_reference.sessions TO am_delivery_receiver_runtime;
GRANT UPDATE(version) ON delivery_reference.policy_versions,delivery_reference.subject_versions,delivery_reference.session_versions TO am_delivery_receiver_runtime;
GRANT UPDATE(revoked) ON delivery_reference.sessions TO am_delivery_receiver_runtime;
SQL
