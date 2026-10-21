using Monergy.Platform;
namespace Monergy.Services.Audit.Application;

public sealed record AuditLogQuery(string Search="",string Action="all",string Outcome="all",string? ActorId=null,string? CustomerId=null,string? From=null,string? To=null,string? After=null);
public sealed record AuditLogEntry(string TenantId,string EvidenceId,string? RequestId,string Phase,string? ActorId,string ActorType,string? CustomerId,string Action,string Source,string ResourceType,string? ResourceId,string? DocumentId,DateTimeOffset OccurredAt,DateTimeOffset RecordedAt,string Outcome,string Reason,string? CorrelationId,string? SourceContractId,string? IpAddress=null,string? Device=null);
public sealed record AuditLogPage(string TenantId,IReadOnlyList<AuditLogEntry> Items,string? NextCursor,long Matching,string Coverage);
public interface IProductAuditLog
{
 Task<ProductAuditRecord> AppendActivityAsync(ProductAuditRecord r,CancellationToken ct);
 Task<AuditLogPage> SearchAsync(string tenant,AuditLogQuery q,CancellationToken ct);
 Task<AuditLogEntry?> ReadActivityAsync(string tenant,string id,CancellationToken ct);
}
public static class ProductAuditLog
{
 public const string Coverage="Recorded product requests, responses and domain events. A request without a matching result is unresolved. Older records may have Unrecorded identity fields. IP/device collection is disabled.";
 public static void Query(AuditLogQuery q)
 {
  if(q.Search!=q.Search.Trim()||q.Search.Length>128||q.Search.Any(char.IsControl)||q.Action!="all"&&!ProductAuditProtocol.Actions.Contains(q.Action)&&q.Action!="DOMAIN_EVENT"||
   q.Outcome is not ("all" or "REQUESTED" or "HTTP_SUCCEEDED" or "HTTP_FAILED" or "ACCEPTED" or "UNKNOWN" or "RECORDED")||
   q.ActorId is not null&&!TenantAccessClient.Identifier(q.ActorId,128)||q.CustomerId is not null&&!TenantAccessClient.Identifier(q.CustomerId,128)||q.After is not null&&!TenantAccessClient.Identifier(q.After,240)||
   !Date(q.From)||!Date(q.To)||q.From is not null&&q.To is not null&&Parse(q.From)>Parse(q.To))throw new TenantBoundaryException("AUDIT_QUERY_INVALID",400);
 }
 public static DateTimeOffset Parse(string s)=>DateTimeOffset.ParseExact(s,"yyyy-MM-ddTHH:mm:ss.fffZ",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal);
 private static bool Date(string? s)=>s is null||DateTimeOffset.TryParseExact(s,"yyyy-MM-ddTHH:mm:ss.fffZ",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out _);
 public static bool Matches(AuditLogEntry r,AuditLogQuery q)=>(q.Action=="all"||r.Action==q.Action)&&(q.Outcome=="all"||r.Outcome==q.Outcome)&&(q.ActorId is null||r.ActorId==q.ActorId)&&(q.CustomerId is null||r.CustomerId==q.CustomerId)&&(q.From is null||r.OccurredAt>=Parse(q.From))&&(q.To is null||r.OccurredAt<=Parse(q.To))&&new[]{r.EvidenceId,r.RequestId,r.ResourceId,r.DocumentId,r.CorrelationId,r.Source}.Any(x=>x?.Contains(q.Search,StringComparison.OrdinalIgnoreCase)==true);
 public static AuditLogEntry View(ProductAuditRecord r,DateTimeOffset recorded)=>new(r.TenantId,"activity-"+r.EventId,r.RequestId,r.Phase,r.ActorId,r.ActorType,r.CustomerId,r.Action,r.Source,r.ResourceType,r.ResourceId,r.DocumentId,r.OccurredAt,recorded,r.Outcome,r.Reason,r.RequestId,null);
 public static void Bound(AuditLogEntry r,string tenant)
 {
  var domain=r.Phase=="DOMAIN";
  if(domain ? r.Action!="DOMAIN_EVENT"||r.Outcome!="RECORDED"||r.RequestId is not null :
   r.Phase is not ("REQUESTED" or "RESULT")||!ProductAuditProtocol.Actions.Contains(r.Action)||
   r.RequestId is null||!TenantAccessClient.Identifier(r.RequestId,64)||r.Source!="Product Gateway"||
   (r.Phase=="REQUESTED" ? r.Outcome!="REQUESTED" : r.Outcome is not ("HTTP_SUCCEEDED" or "HTTP_FAILED" or "ACCEPTED" or "UNKNOWN")))
   throw new TenantBoundaryException("AUDIT_RESULT_INVALID",503);
  if(r.ActorType is not ("USER" or "UNAUTHENTICATED" or "UNRECORDED")||(r.ActorType=="USER")!=(r.ActorId is not null)||
   !TenantAccessClient.Identifier(r.ResourceType,100)||r.CorrelationId is not null&&!TenantAccessClient.Identifier(r.CorrelationId,240)||
   r.SourceContractId is not null&&!TenantAccessClient.Identifier(r.SourceContractId,240))throw new TenantBoundaryException("AUDIT_RESULT_INVALID",503);
  if(r.TenantId!=tenant||!TenantAccessClient.Identifier(r.EvidenceId,240)||r.RecordedAt<DateTimeOffset.UnixEpoch||r.OccurredAt<DateTimeOffset.UnixEpoch||r.IpAddress is not null||r.Device is not null||r.ActorId is not null&&!TenantAccessClient.Identifier(r.ActorId,128)||r.CustomerId is not null&&!TenantAccessClient.Identifier(r.CustomerId,128)||r.DocumentId is not null&&!TenantAccessClient.Identifier(r.DocumentId,160)||r.ResourceId is not null&&!TenantAccessClient.Identifier(r.ResourceId,200)||r.Source.Length>100||r.Source.Any(char.IsControl)||!TenantAccessClient.Identifier(r.Action,100)||!TenantAccessClient.Identifier(r.Reason,100))throw new TenantBoundaryException("AUDIT_RESULT_INVALID",503);}
}
