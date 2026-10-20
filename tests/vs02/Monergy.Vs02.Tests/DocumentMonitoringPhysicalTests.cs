using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Vs02.Tests;
public sealed class DocumentMonitoringPhysicalTests
{
 [Fact,Trait("Category","Physical")]
 public async Task RecordedTenantAndScanSurviveRestartWithScopedSearchCountsVersionsAndAppendOnlyGrants()
 {
  var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"},{"Monergy:Persistence:Provider","POSTGRESQL_S3"},{"Monergy:Persistence:Evidence:RuntimeConnection",Environment.GetEnvironmentVariable("T193_EVIDENCE_RUNTIME_CONNECTION")}}).Build();
  var suffix=Guid.NewGuid().ToString("N");var now=DateTimeOffset.UtcNow;var hash=new string('a',64);var row=new EvidenceVersionRecord("monitor-document-"+suffix,"monitor-version-"+suffix,"monitor-evidence-"+suffix,"monitor-customer-"+suffix,0,"MANUAL_UPLOAD","statement.pdf","application/pdf","private-reference",hash,now,now,1,"private-key","private-storage-version","T001",new(hash,"1.4.6","27800",now.AddHours(-1),now));var identity=new EvidenceOperationIdentity("CreateDocumentVersion","1.0.0",row.CustomerId,suffix);
  await using(var repo=new PostgresEvidenceRepository(cfg)){await repo.SaveVersionAsync(identity,row,_=>[],default);}
  await using(var restarted=new PostgresEvidenceRepository(cfg))
  {
   var saved=(await restarted.GetVersionAsync(row.DocumentVersionId,default))!;Assert.Equal("T001",saved.TenantId);Assert.Equal(row.MalwareCheck,saved.MalwareCheck);var page=await restarted.MonitorAsync(new("T001",[]),new(CustomerId:row.CustomerId),default);Assert.Equal(1,page.Total);Assert.Equal(1,page.VerifiedAtIntake);Assert.Equal("VERIFIED_AT_INTAKE",Assert.Single(page.Items).ScanStatus);Assert.Null(await restarted.MonitorReadAsync(new("T002",[]),row.DocumentVersionId,default));Assert.Empty((await restarted.MonitorAsync(new("T001",[]),new("%",CustomerId:row.CustomerId),default)).Items);Assert.False((await restarted.SaveVersionAsync(identity,row,_=>[],default)).Created);
  }
  await using var db=new NpgsqlConnection(PhysicalPersistenceGuard.Require(cfg,"Monergy:Persistence:Evidence:RuntimeConnection"));await db.OpenAsync();Assert.False(await db.ExecuteScalarAsync<bool>("SELECT has_table_privilege(current_user,'evidence.document_versions','UPDATE,DELETE,TRUNCATE');"));
 }
}
