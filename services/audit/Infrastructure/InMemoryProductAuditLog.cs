using Monergy.Platform;
using Monergy.Services.Audit.Application;
namespace Monergy.Services.Audit.Infrastructure;
public sealed class InMemoryProductAuditLog:IProductAuditLog
{
 private readonly object gate=new();private readonly Dictionary<string,(ProductAuditRecord Record,DateTimeOffset At)> records=new();
 public Task<ProductAuditRecord> AppendActivityAsync(ProductAuditRecord r,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();ProductAuditProtocol.Validate(r);lock(gate){var key=r.TenantId+"/"+r.EventId;if(records.TryGetValue(key,out var old)){if(old.Record!=r)throw new TenantBoundaryException("AUDIT_EVENT_CONFLICT",409);return Task.FromResult(old.Record);}if(r.Phase=="RESULT"&&(!records.TryGetValue(r.TenantId+"/"+r.RequestId+":REQUESTED",out var begin)||!Same(begin.Record,r)))throw new TenantBoundaryException("AUDIT_REQUEST_MISMATCH",409);records.Add(key,(r,DateTimeOffset.UtcNow));return Task.FromResult(r);}}
 public static bool Same(ProductAuditRecord a,ProductAuditRecord b)=>a.TenantId==b.TenantId&&a.RequestId==b.RequestId&&a.Action==b.Action&&a.Source==b.Source&&a.ResourceType==b.ResourceType&&a.ResourceId==b.ResourceId&&(a.DocumentId is null||a.DocumentId==b.DocumentId)&&(a.CustomerId is null||a.CustomerId==b.CustomerId)&&(a.ActorId==b.ActorId&&a.ActorType==b.ActorType||a.Action=="LOGIN"&&a.ActorId is null&&b.ActorType=="USER")&&b.OccurredAt>=a.OccurredAt;
 public Task<AuditLogPage> SearchAsync(string tenant,AuditLogQuery q,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();ProductAuditLog.Query(q);lock(gate){var all=records.Values.Select(x=>ProductAuditLog.View(x.Record,x.At)).Where(x=>x.TenantId==tenant&&ProductAuditLog.Matches(x,q)).OrderByDescending(x=>x.RecordedAt).ThenByDescending(x=>x.EvidenceId,StringComparer.Ordinal).ToArray();int offset=0;if(q.After is not null){offset=Array.FindIndex(all,x=>x.EvidenceId==q.After)+1;if(offset==0)throw new TenantBoundaryException("AUDIT_CURSOR_INVALID",400);}var page=all.Skip(offset).Take(51).ToArray();return Task.FromResult(new AuditLogPage(tenant,page.Take(50).ToArray(),page.Length>50?page[49].EvidenceId:null,all.LongLength,ProductAuditLog.Coverage));}}
 public Task<AuditLogEntry?> ReadActivityAsync(string tenant,string id,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();lock(gate)return Task.FromResult(records.Values.Select(x=>ProductAuditLog.View(x.Record,x.At)).SingleOrDefault(x=>x.TenantId==tenant&&x.EvidenceId==id));}
 public Task<SignInSecuritySummary> ReviewSignInSecurityAsync(string tenant,DateTimeOffset observedAt,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();var start=observedAt.AddMinutes(-ProductAuditLog.SignInWindowMinutes);lock(gate){var failures=records.Values.LongCount(x=>x.Record.TenantId==tenant&&x.Record.Action=="LOGIN"&&x.Record.Phase=="RESULT"&&x.Record.Outcome=="HTTP_FAILED"&&x.Record.OccurredAt>=start&&x.Record.OccurredAt<=observedAt);return Task.FromResult(ProductAuditLog.SignInSummary(tenant,observedAt,failures));}}
 public Task<OperationalOutcomeSummary> ReviewOperationalOutcomesAsync(string tenant,DateTimeOffset observedAt,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();var start=observedAt.AddMinutes(-ProductAuditLog.OperationalWindowMinutes);lock(gate){var unconfirmed=records.Values.LongCount(x=>x.Record.TenantId==tenant&&x.Record.Phase=="RESULT"&&x.Record.Outcome=="UNKNOWN"&&x.Record.OccurredAt>=start&&x.Record.OccurredAt<=observedAt);return Task.FromResult(ProductAuditLog.OperationalSummary(tenant,observedAt,unconfirmed));}}
}
