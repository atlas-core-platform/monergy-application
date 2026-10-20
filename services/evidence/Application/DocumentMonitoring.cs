using Monergy.Platform;
namespace Monergy.Services.Evidence.Application;

public sealed record DocumentMonitorScope(string TenantId,IReadOnlyList<string> ForeignCustomers)
{public bool Owns(EvidenceVersionRecord row)=>row.TenantId==TenantId&&!ForeignCustomers.Contains(row.CustomerId,StringComparer.Ordinal);}
public sealed record DocumentMonitorQuery(string Search="",string Scan="all",string? CustomerId=null,string? After=null,string? DocumentId=null);
public sealed record MonitoredDocument(string TenantId,string DocumentId,string DocumentVersionId,string CustomerId,int Version,string FileName,string ContentType,long ContentLength,DateTimeOffset ReceivedAt,DateTimeOffset RegisteredAt,string ScanStatus,EvidenceMalwareCheck? ScanEvidence);
public sealed record DocumentMonitorPage(string TenantId,IReadOnlyList<MonitoredDocument> Items,string? NextCursor,long Matching,long Total,long VerifiedAtIntake,long Unrecorded);
public interface IDocumentMonitoringRepository
{
 Task<DocumentMonitorPage> MonitorAsync(DocumentMonitorScope scope,DocumentMonitorQuery query,CancellationToken ct);
 Task<MonitoredDocument?> MonitorReadAsync(DocumentMonitorScope scope,string versionId,CancellationToken ct);
}
public static class DocumentMonitoring
{
 public static bool Scanned(EvidenceVersionRecord row)=>EvidenceMalwarePolicy.Verified(row.MalwareCheck,row.ContentSha256,row.CreatedAt);
 public static MonitoredDocument View(DocumentMonitorScope scope,EvidenceVersionRecord row)=>new(scope.TenantId,row.DocumentId,row.DocumentVersionId,row.CustomerId,row.Version,Text(row.OriginalFileName,"Document"),Text(row.ContentType,"Unavailable"),row.ContentLength,row.ReceivedAt,row.CreatedAt,Scanned(row)?"VERIFIED_AT_INTAKE":"UNRECORDED",Scanned(row)?row.MalwareCheck:null);
 private static string Text(string value,string fallback)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=255&&!value.Any(char.IsControl)?value:fallback;
 public static void Query(DocumentMonitorQuery query)
 {if(query.Search!=query.Search.Trim()||query.Search.Length>128||query.Search.Any(char.IsControl)||query.Scan is not ("all" or "VERIFIED_AT_INTAKE" or "UNRECORDED")||query.CustomerId is not null&&!TenantAccessClient.Identifier(query.CustomerId,128)||query.After is not null&&!TenantAccessClient.Identifier(query.After,160)||query.DocumentId is not null&&!TenantAccessClient.Identifier(query.DocumentId,160))throw new TenantBoundaryException("DOCUMENT_MONITOR_QUERY_INVALID",400);}
}
