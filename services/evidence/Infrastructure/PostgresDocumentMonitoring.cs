using System.Data;
using Dapper;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
namespace Monergy.Services.Evidence.Infrastructure;
public sealed partial class PostgresEvidenceRepository:IDocumentMonitoringRepository
{
 public async Task<DocumentMonitorPage> MonitorAsync(DocumentMonitorScope scope,DocumentMonitorQuery query,CancellationToken ct)
 {
  DocumentMonitoring.Query(query);await using var db=await dataSource.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  EvidenceVersionRecord? cursor=null;if(query.After is not null){cursor=await FindVersionAsync(db,tx,query.After,ct);if(cursor is null||!scope.Owns(cursor)||query.CustomerId is not null&&cursor.CustomerId!=query.CustomerId||query.DocumentId is not null&&cursor.DocumentId!=query.DocumentId)throw new TenantBoundaryException("DOCUMENT_MONITOR_CURSOR_INVALID",400);}
  var args=new{scope.TenantId,Foreign=scope.ForeignCustomers.ToArray(),Customer=query.CustomerId,Document=query.DocumentId,query.Search,query.Scan,AfterId=cursor?.DocumentVersionId,AfterAt=cursor?.CreatedAt};
  const string basis=" WHERE tenant_id=@TenantId AND customer_id<>ALL(@Foreign) AND (CAST(@Customer AS text) IS NULL OR customer_id=@Customer) AND (CAST(@Document AS text) IS NULL OR document_id=@Document)";
  var total=await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM evidence.document_versions"+basis,args,tx,cancellationToken:ct));var scanned=await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM evidence.document_versions"+basis+" AND intake_scan_verified",args,tx,cancellationToken:ct));
  var filter=basis+" AND (@Scan='all' OR intake_scan_verified=(@Scan='VERIFIED_AT_INTAKE')) AND (strpos(lower(original_file_name),lower(@Search))>0 OR strpos(lower(document_id),lower(@Search))>0 OR strpos(lower(document_version_id),lower(@Search))>0)";
  var matching=await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM evidence.document_versions"+filter,args,tx,cancellationToken:ct));
  var rows=(await db.QueryAsync<VersionRow>(new CommandDefinition("SELECT "+VersionColumns+" FROM evidence.document_versions"+filter+" AND (CAST(@AfterId AS text) IS NULL OR (created_at,document_version_id COLLATE \"C\")<(@AfterAt,@AfterId COLLATE \"C\")) ORDER BY created_at DESC,document_version_id COLLATE \"C\" DESC LIMIT 51",args,tx,cancellationToken:ct))).Select(ToRecord).ToArray();await tx.CommitAsync(ct);
  return new(scope.TenantId,rows.Take(50).Select(x=>DocumentMonitoring.View(scope,x)).ToArray(),rows.Length>50?rows[49].DocumentVersionId:null,matching,total,scanned,total-scanned);
 }
 public async Task<MonitoredDocument?> MonitorReadAsync(DocumentMonitorScope scope,string versionId,CancellationToken ct)
 {await using var db=await dataSource.OpenConnectionAsync(ct);var row=await FindVersionAsync(db,null,versionId,ct);return row is not null&&scope.Owns(row)?DocumentMonitoring.View(scope,row):null;}
}
