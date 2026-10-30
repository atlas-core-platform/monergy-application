using System.Data;
using System.Text.Json;
using Dapper;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
namespace Monergy.Services.Audit.Infrastructure;
public sealed partial class PostgresTenantAccessAudit:IProductAuditLog
{
 private static readonly JsonSerializerOptions ActivityJson=new(JsonSerializerDefaults.Web);
 public async Task<ProductAuditRecord> AppendActivityAsync(ProductAuditRecord r,CancellationToken ct)
 {
  ProductAuditProtocol.Validate(r);await using var db=await OpenAsync(r.TenantId,ct);await using var tx=await db.BeginTransactionAsync(ct);
  if(await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege(current_user,'audit.product_activity','UPDATE,DELETE,TRUNCATE');",transaction:tx,cancellationToken:ct)))throw new TenantBoundaryException("AUDIT_GRANTS_INVALID",503);
  await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Id,0));",new{Id=r.RequestId},tx,cancellationToken:ct));
  var old=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT record::text FROM audit.product_activity WHERE event_id=@EventId;",r,tx,cancellationToken:ct));
  if(old is not null){var saved=JsonSerializer.Deserialize<ProductAuditRecord>(old,ActivityJson)!;if(saved!=r)throw new TenantBoundaryException("AUDIT_EVENT_CONFLICT",409);await tx.CommitAsync(ct);return saved;}
  if(r.Phase=="RESULT"){var begin=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT record::text FROM audit.product_activity WHERE event_id=@Id;",new{Id=r.RequestId+":REQUESTED"},tx,cancellationToken:ct));if(begin is null||!InMemoryProductAuditLog.Same(JsonSerializer.Deserialize<ProductAuditRecord>(begin,ActivityJson)!,r))throw new TenantBoundaryException("AUDIT_REQUEST_MISMATCH",409);}
  await db.ExecuteAsync(new CommandDefinition("INSERT INTO audit.product_activity(event_id,record) VALUES(@EventId,CAST(@Json AS jsonb));",new{r.EventId,Json=JsonSerializer.Serialize(r,ActivityJson)},tx,cancellationToken:ct));await tx.CommitAsync(ct);return r;
 }
 private static async Task Guard(Npgsql.NpgsqlConnection db,CancellationToken ct)
 {if(await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege(current_user,'audit.product_activity','UPDATE,DELETE,TRUNCATE');",cancellationToken:ct)))throw new TenantBoundaryException("AUDIT_GRANTS_INVALID",503);}
 private const string Ledger="""
 WITH ledger AS (
 SELECT jsonb_build_object('tenantId',@Tenant,'evidenceId','activity-'||a.event_id,'requestId',a.record->>'requestId','phase',a.record->>'phase',
 'actorId',a.record->>'actorId','actorType',a.record->>'actorType','customerId',a.record->>'customerId','action',a.record->>'action',
 'source',a.record->>'source','resourceType',a.record->>'resourceType','resourceId',a.record->>'resourceId','documentId',a.record->>'documentId',
 'occurredAt',a.record->>'occurredAt','recordedAt',a.recorded_at,'outcome',a.record->>'outcome','reason',a.record->>'reason',
 'correlationId',a.record->>'requestId','sourceContractId',NULL,'ipAddress',NULL,'device',NULL) AS entry,a.recorded_at AS at,'activity-'||a.event_id AS id
 FROM audit.product_activity a WHERE a.record->>'tenantId'=@Tenant
 UNION ALL
 SELECT jsonb_build_object('tenantId',@Tenant,'evidenceId',e.audit_evidence_id,'requestId',NULL,'phase','DOMAIN',
 'actorId',i.envelope->>'initiatorActorId','actorType',CASE WHEN i.event_id IS NULL THEN 'UNRECORDED' ELSE 'USER' END,
 'customerId',NULL,'action','DOMAIN_EVENT','source',e.producer,'resourceType',e.subject_type,'resourceId',e.subject_id,'documentId',NULL,
 'occurredAt',e.occurred_at,'recordedAt',e.recorded_at,'outcome','RECORDED','reason',e.event_name,'correlationId',e.correlation_id,
 'sourceContractId',e.source_contract_id,'ipAddress',NULL,'device',NULL),e.recorded_at,e.audit_evidence_id
 FROM audit.evidence e LEFT JOIN audit.access_event_inbox i USING(audit_evidence_id)
 )
 """;
 private const string Filter="""
 WHERE (@Action='all' OR entry->>'action'=@Action) AND (@Outcome='all' OR entry->>'outcome'=@Outcome)
 AND (CAST(@Actor AS text) IS NULL OR entry->>'actorId'=@Actor) AND (CAST(@Customer AS text) IS NULL OR entry->>'customerId'=@Customer)
 AND (CAST(@From AS timestamptz) IS NULL OR CAST(entry->>'occurredAt' AS timestamptz)>=@From)
 AND (CAST(@To AS timestamptz) IS NULL OR CAST(entry->>'occurredAt' AS timestamptz)<=@To)
 AND (strpos(lower(id),lower(@Search))>0 OR strpos(lower(COALESCE(entry->>'requestId','')),lower(@Search))>0
 OR strpos(lower(COALESCE(entry->>'resourceId','')),lower(@Search))>0 OR strpos(lower(COALESCE(entry->>'documentId','')),lower(@Search))>0
 OR strpos(lower(COALESCE(entry->>'correlationId','')),lower(@Search))>0 OR strpos(lower(entry->>'source'),lower(@Search))>0)
 """;
 // Ensure raw SQL fragments cannot concatenate into invalid tokens (ledgerWHERE, )SELECT).
 private static string SearchStatement(string projection) => Ledger + "\n" + projection + "\n" + Filter;
 public async Task<AuditLogPage> SearchAsync(string tenant,AuditLogQuery q,CancellationToken ct)
 {
  ProductAuditLog.Query(q);await using var db=await OpenAsync(tenant,ct);await Guard(db,ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  var args=new{Tenant=tenant,q.Action,q.Outcome,Actor=q.ActorId,Customer=q.CustomerId,From=q.From is null?(DateTimeOffset?)null:ProductAuditLog.Parse(q.From),To=q.To is null?(DateTimeOffset?)null:ProductAuditLog.Parse(q.To),q.Search,After=q.After};
  if(q.After is not null&&!await db.ExecuteScalarAsync<bool>(new CommandDefinition(SearchStatement("SELECT EXISTS(SELECT 1 FROM ledger")+" AND id=@After);",args,tx,cancellationToken:ct)))throw new TenantBoundaryException("AUDIT_CURSOR_INVALID",400);
  var count=await db.ExecuteScalarAsync<long>(new CommandDefinition(SearchStatement("SELECT count(*) FROM ledger"),args,tx,cancellationToken:ct));
  var rows=(await db.QueryAsync<string>(new CommandDefinition(SearchStatement("SELECT entry::text FROM ledger")+" AND (CAST(@After AS text) IS NULL OR (at,id COLLATE \"C\")<(SELECT at,id COLLATE \"C\" FROM ledger WHERE id=@After)) ORDER BY at DESC,id COLLATE \"C\" DESC LIMIT 51;",args,tx,cancellationToken:ct))).Select(x=>JsonSerializer.Deserialize<AuditLogEntry>(x,ActivityJson)!).ToArray();await tx.CommitAsync(ct);
  return new(tenant,rows.Take(50).ToArray(),rows.Length>50?rows[49].EvidenceId:null,count,ProductAuditLog.Coverage);
 }
 public async Task<AuditLogEntry?> ReadActivityAsync(string tenant,string id,CancellationToken ct)
 {await using var db=await OpenAsync(tenant,ct);await Guard(db,ct);var json=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(Ledger+"\nSELECT entry::text FROM ledger WHERE id=@Id;",new{Tenant=tenant,Id=id},cancellationToken:ct));return json is null?null:JsonSerializer.Deserialize<AuditLogEntry>(json,ActivityJson);}
 public async Task<SignInSecuritySummary> ReviewSignInSecurityAsync(string tenant,DateTimeOffset observedAt,CancellationToken ct)
 {
  await using var db=await OpenAsync(tenant,ct);await Guard(db,ct);var start=observedAt.AddMinutes(-ProductAuditLog.SignInWindowMinutes);
  var failures=await db.ExecuteScalarAsync<long>(new CommandDefinition("""
   SELECT count(*) FROM audit.product_activity
   WHERE record->>'tenantId'=@Tenant AND record->>'action'='LOGIN' AND record->>'phase'='RESULT'
   AND record->>'outcome'='HTTP_FAILED' AND CAST(record->>'occurredAt' AS timestamptz)>=@Start
   AND CAST(record->>'occurredAt' AS timestamptz)<=@ObservedAt;
   """,new{Tenant=tenant,Start=start,ObservedAt=observedAt},cancellationToken:ct));
  return ProductAuditLog.SignInSummary(tenant,observedAt,failures);
 }
}
