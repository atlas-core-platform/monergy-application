using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.JobManagement.Application;

public sealed record AdminJobScope(string TenantId,IReadOnlyList<string> ForeignCustomers,bool AllowRetry=true)
{
 public string Workload=>"local-uat-workspace:"+TenantId;
 public bool Owns(DurableJobRecord job)=>!ForeignCustomers.Contains(job.CustomerId,StringComparer.Ordinal)&&job.Security.Workload.WorkloadIdentityId==Workload&&job.Security.Access.CustomerId==job.CustomerId;
}
public sealed record AdminJobQuery(string Search="",string State="all",string? CustomerId=null,string? After=null);
public sealed record AdminJobSummary(string TenantId,string JobId,string CustomerId,string Task,string State,bool CancellationPending,int Attempt,int RetryCount,int ReplayCount,DateTimeOffset RequestedAt,DateTimeOffset UpdatedAt,DateTimeOffset? NextAttemptAt,DateTimeOffset? TerminalAt,string? FailureCode,bool NeedsOwnerReview,bool CanRetry,bool CanCancel,string Revision);
public sealed record AdminJobCounts(long Queued,long Processing,long Completed,long Failed,long Retrying,long Cancelled);
public sealed record AdminJobPage(string TenantId,IReadOnlyList<AdminJobSummary> Items,string? NextCursor,long Matching,AdminJobCounts Counts);
public sealed record AdminJobAttempt(int Attempt,DateTimeOffset StartedAt,DateTimeOffset? EndedAt,string? Outcome,string? FailureCode,bool Retryable);
public sealed record AdminJobAttempts(string TenantId,string JobId,IReadOnlyList<AdminJobAttempt> Items,int? NextBeforeAttempt);
public sealed record AdminJobChange(string OperationId,string ExpectedRevision,string Action,string Reason);
public sealed record AdminJobReceipt(string TenantId,string JobId,string OperationId,string Action,string Reason,string RecordedBy,DateTimeOffset RecordedAt,AdminJobSummary Before,AdminJobSummary After);
public sealed record AdminJobHistory(string TenantId,string JobId,IReadOnlyList<AdminJobReceipt> Items,string? NextBeforeId);
public interface IJobAdministrationRepository
{
 Task<AdminJobPage> ListAdminAsync(AdminJobScope scope,AdminJobQuery query,CancellationToken ct);
 Task<AdminJobSummary?> ReadAdminAsync(AdminJobScope scope,string jobId,CancellationToken ct);
 Task<AdminJobAttempts> AttemptsAdminAsync(AdminJobScope scope,string jobId,int? before,CancellationToken ct);
 Task<AdminJobReceipt> ChangeAdminAsync(AdminJobScope scope,string actor,string jobId,AdminJobChange change,DateTimeOffset now,CancellationToken ct);
 Task<AdminJobReceipt?> ReceiptAdminAsync(AdminJobScope scope,string jobId,string operation,CancellationToken ct);
 Task<AdminJobHistory> HistoryAdminAsync(AdminJobScope scope,string jobId,string? before,CancellationToken ct);
}
public static class JobAdministration
{
 public static readonly string[] States=["QUEUED","PROCESSING","COMPLETED","FAILED","RETRYING","CANCELLED"];
 public static string State(DurableJobRecord job)=>job.State switch
 {JobState.Scheduled=>job.ReplayCount>0||job.RetryCount>0?"RETRYING":"QUEUED",JobState.Running or JobState.CancellationRequested=>"PROCESSING",JobState.Completed=>"COMPLETED",JobState.Cancelled=>"CANCELLED",JobState.Failed when job.Retryable&&job.NextAttemptAt is not null&&!job.ReconciliationRequired=>"RETRYING",_=>"FAILED"};
 public static bool CanRetry(DurableJobRecord job)=>job.JobName=="document-processing"&&job.State==JobState.Failed&&job.Retryable&&!job.ReconciliationRequired&&job.LeaseToken is null;
 public static bool CanCancel(DurableJobRecord job)=>!job.ReconciliationRequired&&((job.State is JobState.Scheduled or JobState.Running)||job.State==JobState.Failed&&job.Retryable&&job.LeaseToken is null);
 public static AdminJobSummary Summary(AdminJobScope scope,DurableJobRecord job)=>new(scope.TenantId,job.JobId,job.CustomerId,job.JobName=="document-processing"?"Document processing":"Background task",State(job),job.State==JobState.CancellationRequested,job.Attempt,job.RetryCount,job.ReplayCount,job.RequestedAt,job.UpdatedAt,job.NextAttemptAt,job.TerminalAt,Safe(job.FailureCode),job.ReconciliationRequired,scope.AllowRetry&&CanRetry(job),CanCancel(job),Revision(job));
 public static DateTimeOffset Time(DateTimeOffset value)=>new(value.UtcTicks-value.UtcTicks%10,TimeSpan.Zero);
 public static string Revision(DurableJobRecord job)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(job,ContractJson.Options)));
 public static string? Safe(string? value)=>value is null?null:TenantAccessClient.Identifier(value,160)?value:"job.metadata.unavailable";
 public static void Query(AdminJobQuery query)
 {if(query.Search!=query.Search.Trim()||query.Search.Length>128||query.Search.Any(char.IsControl)||query.State!="all"&&!States.Contains(query.State,StringComparer.Ordinal)||query.CustomerId is not null&&!TenantAccessClient.Identifier(query.CustomerId,128)||query.After is not null&&!TenantAccessClient.Identifier(query.After,128))throw new TenantBoundaryException("PROCESSING_QUERY_INVALID",400);}
 public static void Change(AdminJobChange change)
 {if(!Guid.TryParseExact(change.OperationId,"N",out _)||change.OperationId.Any(x=>x is >='A' and <='F')||change.ExpectedRevision.Length!=64||change.ExpectedRevision.Any(x=>!char.IsAsciiHexDigit(x)||x is >='A' and <='F')||change.Action is not ("retry" or "cancel")||string.IsNullOrWhiteSpace(change.Reason)||change.Reason!=change.Reason.Trim()||change.Reason.Length>500||change.Reason.Any(char.IsControl))throw new TenantBoundaryException("PROCESSING_CHANGE_INVALID",400);}
 public static DurableJobRecord Apply(DurableJobRecord job,AdminJobChange change,DateTimeOffset now)
 {
  Change(change);if(Revision(job)!=change.ExpectedRevision)throw new TenantBoundaryException("PROCESSING_VERSION_CONFLICT",409);
  if(change.Action=="retry")
  {if(!CanRetry(job))throw new TenantBoundaryException("PROCESSING_RETRY_UNAVAILABLE",409);return job with{State=JobState.Scheduled,ReplayCount=checked(job.ReplayCount+1),NextAttemptAt=now,TerminalAt=null,ReconciliationRequired=false,UpdatedAt=now};}
  if(!CanCancel(job))throw new TenantBoundaryException("PROCESSING_CANCEL_UNAVAILABLE",409);
  var immediate=job.State is JobState.Scheduled or JobState.Failed;
  return job with{State=immediate?JobState.Cancelled:JobState.CancellationRequested,FailureCode="job.administrator.cancellation-requested",Retryable=false,NextAttemptAt=null,CancellationRequestedAt=now,TerminalAt=immediate?now:null,UpdatedAt=now};
 }
 public static void Match(AdminJobReceipt receipt,AdminJobScope scope,string actor,string jobId,AdminJobChange change)
 {if(receipt.TenantId!=scope.TenantId||receipt.JobId!=jobId||receipt.RecordedBy!=actor||receipt.OperationId!=change.OperationId||receipt.Action!=change.Action||receipt.Reason!=change.Reason||receipt.Before.Revision!=change.ExpectedRevision||receipt.Before.CustomerId!=receipt.After.CustomerId||receipt.Before.JobId!=jobId||receipt.After.JobId!=jobId||receipt.Before.TenantId!=scope.TenantId||receipt.After.TenantId!=scope.TenantId||scope.ForeignCustomers.Contains(receipt.Before.CustomerId,StringComparer.Ordinal))throw new TenantBoundaryException("PROCESSING_OPERATION_CONFLICT",409);}
 public static AdminJobCounts Counts(IEnumerable<DurableJobRecord> jobs)
 {var all=jobs.ToArray();return new(all.LongCount(x=>State(x)=="QUEUED"),all.LongCount(x=>State(x)=="PROCESSING"),all.LongCount(x=>State(x)=="COMPLETED"),all.LongCount(x=>State(x)=="FAILED"),all.LongCount(x=>State(x)=="RETRYING"),all.LongCount(x=>State(x)=="CANCELLED"));}
}
