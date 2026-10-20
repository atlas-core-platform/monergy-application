using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
namespace Monergy.Services.JobManagement.Infrastructure;

public sealed partial class PostgresJobRepository:IJobAdministrationRepository
{
 private static readonly JsonSerializerOptions AdminJson=new(JsonSerializerDefaults.Web);
 private const string AdminSelect="""
 SELECT job_id AS JobId, job_name AS JobName, job_version AS JobVersion,
                   owner_service AS OwnerService, payload_reference AS PayloadReference,
                   customer_id AS CustomerId, request_id AS RequestId, correlation_id AS CorrelationId,
                   causation_id AS CausationId, security_context::text AS SecurityJson,
                   idempotency_key AS IdempotencyKey, payload_fingerprint AS PayloadFingerprint,
                   state AS State, attempt AS Attempt, retry_count AS RetryCount,
                   replay_count AS ReplayCount,
                   requested_at AS RequestedAt, scheduled_at AS ScheduledAt,
                   next_attempt_at AS NextAttemptAt, cancellation_requested_at AS CancellationRequestedAt,
                   terminal_at AS TerminalAt, failure_code AS FailureCode, retryable AS Retryable,
                   outcome_reference AS OutcomeReference, lease_owner AS LeaseOwner,
                   lease_token AS LeaseToken, lease_expires_at AS LeaseExpiresAt,
                   reconciliation_required AS ReconciliationRequired, updated_at AS UpdatedAt
 FROM job_management.jobs
 """;
 private const string AdminWhere=" WHERE security_context->'workload'->>'workloadIdentityId'=@Workload AND security_context->'access'->>'customerId'=customer_id AND customer_id<>ALL(@ForeignCustomers)";
 private const string StateCase="CASE WHEN state='Scheduled' THEN CASE WHEN replay_count>0 OR retry_count>0 THEN 'RETRYING' ELSE 'QUEUED' END WHEN state IN ('Running','CancellationRequested') THEN 'PROCESSING' WHEN state='Completed' THEN 'COMPLETED' WHEN state='Cancelled' THEN 'CANCELLED' WHEN state='Failed' AND retryable AND next_attempt_at IS NOT NULL AND NOT reconciliation_required THEN 'RETRYING' ELSE 'FAILED' END";
 public async Task<AdminJobPage> ListAdminAsync(AdminJobScope scope,AdminJobQuery query,CancellationToken ct)
 {
  JobAdministration.Query(query);await using var db=await dataSource.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  DurableJobRecord? cursor=null;if(query.After is not null){cursor=await AdminFind(db,tx,scope,query.After,ct);if(query.CustomerId is not null&&cursor.CustomerId!=query.CustomerId)throw new TenantBoundaryException("PROCESSING_JOB_NOT_FOUND",404);}
  var args=new{scope.Workload,ForeignCustomers=scope.ForeignCustomers.ToArray(),query.Search,query.State,Customer=query.CustomerId,AfterAt=cursor?.RequestedAt,AfterId=cursor?.JobId};var basis=AdminWhere+" AND (CAST(@Customer AS text) IS NULL OR customer_id=@Customer)";
  var counts=await db.QuerySingleAsync<AdminJobCounts>(new CommandDefinition("SELECT count(*) FILTER(WHERE code='QUEUED') AS Queued,count(*) FILTER(WHERE code='PROCESSING') AS Processing,count(*) FILTER(WHERE code='COMPLETED') AS Completed,count(*) FILTER(WHERE code='FAILED') AS Failed,count(*) FILTER(WHERE code='RETRYING') AS Retrying,count(*) FILTER(WHERE code='CANCELLED') AS Cancelled FROM(SELECT "+StateCase+" AS code FROM job_management.jobs"+basis+") codes;",args,tx,cancellationToken:ct));
  var filter=basis+" AND strpos(lower(job_id),lower(@Search))>0 AND (@State='all' OR "+StateCase+"=@State)";
  var matching=await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM job_management.jobs"+filter+";",args,tx,cancellationToken:ct));
  var rows=(await db.QueryAsync<JobRow>(new CommandDefinition(AdminSelect+filter+" AND (CAST(@AfterId AS text) IS NULL OR (requested_at,job_id COLLATE \"C\")<(@AfterAt,@AfterId COLLATE \"C\")) ORDER BY requested_at DESC,job_id COLLATE \"C\" DESC LIMIT 51;",args,tx,cancellationToken:ct))).Select(x=>x.ToRecord()).ToArray();
  await tx.CommitAsync(ct);return new(scope.TenantId,rows.Take(50).Select(x=>JobAdministration.Summary(scope,x)).ToArray(),rows.Length>50?rows[49].JobId:null,matching,counts);
 }
 private static async Task<DurableJobRecord> AdminFind(NpgsqlConnection db,NpgsqlTransaction? tx,AdminJobScope scope,string jobId,CancellationToken ct,bool locked=false)
 {var job=await FindAsync(db,tx,jobId,ct,locked);if(job is null||!scope.Owns(job))throw new TenantBoundaryException("PROCESSING_JOB_NOT_FOUND",404);return job;}
 public async Task<AdminJobSummary?> ReadAdminAsync(AdminJobScope scope,string jobId,CancellationToken ct)
 {await using var db=await dataSource.OpenConnectionAsync(ct);var job=await FindAsync(db,null,jobId,ct);return job is not null&&scope.Owns(job)?JobAdministration.Summary(scope,job):null;}
 public async Task<AdminJobAttempts> AttemptsAdminAsync(AdminJobScope scope,string jobId,int? before,CancellationToken ct)
 {await using var db=await dataSource.OpenConnectionAsync(ct);await AdminFind(db,null,scope,jobId,ct);var rows=(await db.QueryAsync<AttemptRow>(new CommandDefinition("SELECT job_id AS JobId,attempt AS Attempt,started_at AS StartedAt,ended_at AS EndedAt,outcome AS Outcome,failure_code AS FailureCode,retryable AS Retryable FROM job_management.execution_attempts WHERE job_id=@JobId AND (@Before=0 OR attempt<@Before) ORDER BY attempt DESC LIMIT 51;",new{JobId=jobId,Before=before??0},cancellationToken:ct))).Select(x=>x.ToRecord()).ToArray();return new(scope.TenantId,jobId,rows.Take(50).Select(x=>new AdminJobAttempt(x.Attempt,x.StartedAt,x.EndedAt,JobAdministration.Safe(x.Outcome),JobAdministration.Safe(x.FailureCode),x.Retryable)).ToArray(),rows.Length>50?rows[49].Attempt:null);}
 public async Task<AdminJobReceipt> ChangeAdminAsync(AdminJobScope scope,string actor,string jobId,AdminJobChange change,DateTimeOffset now,CancellationToken ct)
 {
  JobAdministration.Change(change);now=JobAdministration.Time(now);await using var db=await dataSource.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Operation,92));",new{Operation=change.OperationId},tx,cancellationToken:ct));
  var before=await AdminFind(db,tx,scope,jobId,ct,true);
  var prior=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM job_management.administrator_operations WHERE operation_id=@Operation;",new{Operation=Guid.ParseExact(change.OperationId,"N")},tx,cancellationToken:ct));
  if(prior is not null){var saved=AdminParse(prior);JobAdministration.Match(saved,scope,actor,jobId,change);await tx.CommitAsync(ct);return saved;}
  if(change.Action=="retry"&&!scope.AllowRetry)throw new TenantBoundaryException("PROCESSING_RETRY_UNAVAILABLE",409);var after=JobAdministration.Apply(before,change,now);
  await db.ExecuteAsync(new CommandDefinition("UPDATE job_management.jobs SET state=@State,replay_count=@ReplayCount,next_attempt_at=@NextAttemptAt,terminal_at=@TerminalAt,reconciliation_required=@ReconciliationRequired,cancellation_requested_at=@CancellationRequestedAt,failure_code=@FailureCode,retryable=@Retryable,updated_at=@UpdatedAt WHERE job_id=@JobId;",new{after.JobId,State=after.State.ToString(),after.ReplayCount,after.NextAttemptAt,after.TerminalAt,after.ReconciliationRequired,after.CancellationRequestedAt,after.FailureCode,after.Retryable,after.UpdatedAt},tx,cancellationToken:ct));
  var stored=await AdminFind(db,tx,scope,jobId,ct);if(JobAdministration.Revision(stored)!=JobAdministration.Revision(after))throw new TenantBoundaryException("PROCESSING_RESULT_INVALID",503);
  var receipt=new AdminJobReceipt(scope.TenantId,jobId,change.OperationId,change.Action,change.Reason,actor,now,JobAdministration.Summary(scope,before),JobAdministration.Summary(scope,stored));
  await db.ExecuteAsync(new CommandDefinition("INSERT INTO job_management.administrator_operations(operation_id,job_id,recorded_at,payload) VALUES(@Operation,@JobId,@Now,CAST(@Payload AS jsonb));",new{Operation=Guid.ParseExact(change.OperationId,"N"),JobId=jobId,Now=now,Payload=JsonSerializer.Serialize(receipt,AdminJson)},tx,cancellationToken:ct));
  await tx.CommitAsync(ct);return receipt;
 }
 public async Task<AdminJobReceipt?> ReceiptAdminAsync(AdminJobScope scope,string jobId,string operation,CancellationToken ct)
 {await using var db=await dataSource.OpenConnectionAsync(ct);await AdminFind(db,null,scope,jobId,ct);var value=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM job_management.administrator_operations WHERE job_id=@JobId AND operation_id=@Operation;",new{JobId=jobId,Operation=Guid.ParseExact(operation,"N")},cancellationToken:ct));return value is null?null:AdminParse(value);}
 public async Task<AdminJobHistory> HistoryAdminAsync(AdminJobScope scope,string jobId,string? before,CancellationToken ct)
 {
  await using var db=await dataSource.OpenConnectionAsync(ct);await AdminFind(db,null,scope,jobId,ct);AdminJobReceipt? cursor=null;
  if(before is not null){var prior=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM job_management.administrator_operations WHERE job_id=@JobId AND operation_id=@Operation;",new{JobId=jobId,Operation=Guid.ParseExact(before,"N")},cancellationToken:ct));if(prior is null)throw new TenantBoundaryException("PROCESSING_HISTORY_CURSOR_INVALID",400);cursor=AdminParse(prior);}
  var values=(await db.QueryAsync<string>(new CommandDefinition("SELECT payload::text FROM job_management.administrator_operations WHERE job_id=@JobId AND (CAST(@Before AS uuid) IS NULL OR (recorded_at,operation_id)<(@BeforeAt,@Before)) ORDER BY recorded_at DESC,operation_id DESC LIMIT 51;",new{JobId=jobId,Before=before is null?(Guid?)null:Guid.ParseExact(before,"N"),BeforeAt=cursor?.RecordedAt},cancellationToken:ct))).Select(AdminParse).ToArray();
  return new(scope.TenantId,jobId,values.Take(50).ToArray(),values.Length>50?values[49].OperationId:null);
 }
 private static AdminJobReceipt AdminParse(string value)=>JsonSerializer.Deserialize<AdminJobReceipt>(value,AdminJson)??throw new TenantBoundaryException("PROCESSING_RESULT_INVALID",503);
}
