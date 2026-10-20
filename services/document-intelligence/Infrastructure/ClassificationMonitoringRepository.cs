using Monergy.Platform;
namespace Monergy.Services.DocumentIntelligence.Infrastructure;
public sealed partial class InMemoryDocumentProcessingRepository:IClassificationMonitoringRepository
{
 public Task<ClassificationMonitorPage> MonitorAsync(string tenant,string customer,string version,string? after,CancellationToken ct)
 {ct.ThrowIfCancellationRequested();lock(sync){var all=records.Values.Where(x=>x.TenantId==tenant&&x.CustomerId==customer&&x.DocumentVersionId==version).OrderByDescending(x=>x.ProcessingId,StringComparer.Ordinal).ToArray();var offset=0;if(after is not null){offset=Array.FindIndex(all,x=>x.ProcessingId==after)+1;if(offset==0)throw new TenantBoundaryException("CLASSIFICATION_CURSOR_INVALID",400);}var rows=all.Skip(offset).Take(51).ToArray();return Task.FromResult(new ClassificationMonitorPage(tenant,customer,version,rows.Take(50).Select(x=>new ClassificationMonitorItem(x.ProcessingId,x.State.ToString(),x.FailureCode is null?null:TenantAccessClient.Identifier(x.FailureCode,160)?x.FailureCode:"processing.metadata.unavailable",x.Retryable,x.Attempt,x.UpdatedAt,(x.State==Monergy.Contracts.ProcessingState.Completed?x.Facts:[]).Select(f=>f.FactType.Split('_')[0]).Where(DocumentClassificationMonitoring.Categories.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())).ToArray(),rows.Length>50?rows[49].ProcessingId:null));}}
}
