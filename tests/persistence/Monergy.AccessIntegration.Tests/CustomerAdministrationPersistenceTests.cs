using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.AccessIntegration.Tests;

// Run only in the existing isolated AM05 database harness after all migrations.
[Trait("Category","Physical")]
[Collection("CustomerIdentityPhysicalOwners")]
public sealed class CustomerAdministrationPersistenceTests
{
 [Fact]
 public async Task AvailabilityPersistsAcrossRestartBlocksOwnerReadsAndPreservesOwnershipAndAudit()
 {
  var suffix=Guid.NewGuid().ToString("N");var customer="admin-customer-"+suffix;var actor="ci-"+suffix;
  static string Required(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException(name+" is required.");
  var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:ExecutionZone","CI_EPHEMERAL"},{"Monergy:AccessIntegration:Enabled","true"},{"Monergy:AccessIntegration:CustomerIdentityDatabases:T001",Required("AM05_CI_T001_RUNTIME")},{"Monergy:AccessIntegration:CustomerIdentityDatabases:T002",Required("AM05_CI_T002_RUNTIME")}}).Build();
  await using var owner=new NpgsqlConnection(Required("AM05_CI_T001_OWNER"));await owner.OpenAsync();
  await owner.ExecuteAsync("INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email) VALUES(@Actor,@Email);INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email) VALUES('A900','admin-customer-management@example.test') ON CONFLICT DO NOTHING;INSERT INTO customer_identity.customers(customer_id,display_name,owner_actor_id,ownership_evidence) VALUES(@Customer,'Customer lifecycle test',@Actor,'existing verified owner test fixture');",new{Actor=actor,Email=suffix+"@example.test",Customer=customer});
  try
  {
   var change=new CustomerStatusChange(Guid.NewGuid().ToString("N"),1,false,"Reviewed suspension");CustomerStatusReceipt receipt;
   await using(var sources=new PostgresTenantSessions(config))
   {
    var repo=new PostgresCustomerAdministration(sources);var contexts=new PostgresCustomers(sources);var profile=new PostgresSelfCustomerProfile(sources);
    var directory=await repo.ListAsync("T001",new(Search:suffix),default);Assert.Single(directory.Items);Assert.Equal(customer,directory.Items[0].CustomerId);Assert.True(directory.Total>=1);Assert.Empty((await repo.ListAsync("T002",new(Search:suffix),default)).Items);
    Assert.True((await contexts.ReadOwnedAsync("T001",actor,default))!.Active);Assert.True(await profile.OwnsAsync("T001",actor,customer,default));
    receipt=await repo.ChangeAsync("T001","A900",customer,change,DateTimeOffset.UtcNow,default);
    Assert.False((await contexts.ReadAsync("T001",customer,default))!.Active);Assert.Null(await contexts.ReadOwnedAsync("T001",actor,default));Assert.False(await profile.OwnsAsync("T001",actor,customer,default));Assert.Null(await profile.ReadAsync("T001",actor,default));
    Assert.Equal(2,receipt.After.Version);Assert.Null(await repo.ReadAsync("T002",customer,default));
   }
   await using(var restarted=new PostgresTenantSessions(config))
   {
    var repo=new PostgresCustomerAdministration(restarted);Assert.Equal(receipt,await repo.ChangeAsync("T001","A900",customer,change,DateTimeOffset.UtcNow,default));
    Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repo.ChangeAsync("T001","A900",customer,change with{Reason="Changed payload"},DateTimeOffset.UtcNow,default))).Status);
    var active=await repo.ChangeAsync("T001","A900",customer,new(Guid.NewGuid().ToString("N"),2,true,"Reviewed reactivation"),DateTimeOffset.UtcNow,default);
    Assert.Equal(3,active.After.Version);Assert.NotNull(await new PostgresCustomers(restarted).ReadOwnedAsync("T001",actor,default));Assert.Equal(receipt,await repo.ReceiptAsync("T001",customer,change.OperationId,default));Assert.Equal([3L,2L],(await repo.HistoryAsync("T001",customer,null,default)).Items.Select(x=>x.After.Version));
   }
   var original=await owner.QuerySingleAsync<(bool Active,long Version,string Owner)>("SELECT active,version,owner_actor_id FROM customer_identity.customers WHERE customer_id=@Customer;",new{Customer=customer});Assert.True(original.Active);Assert.Equal(1,original.Version);Assert.Equal(actor,original.Owner);
   await using var runtime=new NpgsqlConnection(Required("AM05_CI_T001_RUNTIME"));await runtime.OpenAsync();
   foreach(var sql in new[]{"UPDATE customer_identity.customer_status_changes SET actor_id='A901' WHERE customer_id=@Customer;","DELETE FROM customer_identity.customer_status_changes WHERE customer_id=@Customer;","TRUNCATE customer_identity.customer_status_changes;","DELETE FROM customer_identity.customer_availability WHERE customer_id=@Customer;","UPDATE customer_identity.customers SET active=false WHERE customer_id=@Customer;","UPDATE customer_identity.customers SET owner_actor_id='A900' WHERE customer_id=@Customer;"})Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>runtime.ExecuteAsync(sql,new{Customer=customer}))).SqlState);
  }
  finally{await owner.ExecuteAsync("DELETE FROM customer_identity.customer_status_changes WHERE customer_id=@Customer;DELETE FROM customer_identity.customer_availability WHERE customer_id=@Customer;DELETE FROM customer_identity.customers WHERE customer_id=@Customer;DELETE FROM customer_identity.tenant_principals WHERE actor_id=@Actor;",new{Customer=customer,Actor=actor});}
 }
}
