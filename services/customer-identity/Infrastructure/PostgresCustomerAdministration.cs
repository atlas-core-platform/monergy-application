using System.Data;
using System.Text.Json;
using Dapper;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;
namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomerAdministration(PostgresTenantSessions sessions):ICustomerAdministrationRepository
{
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private const string Projection="""
 SELECT @Tenant AS TenantId,change.customer_id AS CustomerId,coalesce(p.display_name,change.display_name) AS DisplayName,
 i.normalized_email AS Email,change.secondary_label AS SecondaryLabel,change.active AS Active,change.version AS Version,
 (change.owner_actor_id IS NOT NULL AND change.ownership_evidence IS NOT NULL) AS OwnerLinked,b.active AS ActivationAvailable
 FROM customer_identity.current_customers change
 JOIN customer_identity.customers b ON b.customer_id=change.customer_id
 LEFT JOIN customer_identity.customer_profiles p ON p.customer_id=change.customer_id
 LEFT JOIN customer_identity.tenant_principals i ON i.actor_id=change.owner_actor_id
 """;
 public async Task<AdminCustomerPage> ListAsync(string tenant,AdminCustomerQuery query,CancellationToken ct)
 {
  CustomerAdministration.Query(query);await using var db=await sessions.OpenAsync(tenant,ct);await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  var all=await db.QuerySingleAsync<Counts>(new CommandDefinition("SELECT count(*) AS Total,count(*) FILTER(WHERE active) AS Active,count(*) FILTER(WHERE NOT active) AS Inactive FROM customer_identity.current_customers;",transaction:tx,cancellationToken:ct));
  const string filter=" WHERE (@Status='all' OR change.active=(@Status='active')) AND strpos(lower(concat_ws(' ',coalesce(p.display_name,change.display_name),i.normalized_email,change.secondary_label,change.customer_id)),lower(@Search))>0";
  var args=new{Tenant=tenant,query.Status,query.Search,After=query.After??""};
  var matching=await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM ("+Projection+filter+") matches;",args,tx,cancellationToken:ct));
  var rows=(await db.QueryAsync<AdminCustomer>(new CommandDefinition(Projection+filter+" AND change.customer_id COLLATE \"C\">@After COLLATE \"C\" ORDER BY change.customer_id COLLATE \"C\" LIMIT 51;",args,tx,cancellationToken:ct))).ToArray();
  await tx.CommitAsync(ct);return new(tenant,rows.Take(50).ToArray(),rows.Length>50?rows[49].CustomerId:null,all.Total,all.Active,all.Inactive,matching);
 }
 public async Task<IReadOnlyList<AdminCustomer>> ReadManyAsync(string tenant,IReadOnlyList<string> customers,CancellationToken ct)
 {await using var db=await sessions.OpenAsync(tenant,ct);return (await db.QueryAsync<AdminCustomer>(new CommandDefinition(Projection+" WHERE change.customer_id=ANY(@Customers);",new{Tenant=tenant,Customers=customers.ToArray()},cancellationToken:ct))).ToArray();}
 public async Task<AdminCustomer?> ReadAsync(string tenant,string customer,CancellationToken ct)
 {await using var db=await sessions.OpenAsync(tenant,ct);return await Read(db,null,tenant,customer,ct);}
 private static Task<AdminCustomer?> Read(NpgsqlConnection db,NpgsqlTransaction? tx,string tenant,string customer,CancellationToken ct)=>db.QuerySingleOrDefaultAsync<AdminCustomer>(new CommandDefinition(Projection+" WHERE change.customer_id=@Id;",new{Tenant=tenant,Id=customer},tx,cancellationToken:ct));
 public async Task<CustomerStatusReceipt> ChangeAsync(string tenant,string actor,string customer,CustomerStatusChange change,DateTimeOffset now,CancellationToken ct)
 {
  CustomerAdministration.Change(change);await using var db=await sessions.OpenAsync(tenant,ct);await using var tx=await db.BeginTransactionAsync(ct);
  await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Operation,91));SELECT pg_advisory_xact_lock(hashtextextended(@Id,44));",new{Operation=change.OperationId,Id=customer},tx,cancellationToken:ct));
  var prior=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.customer_status_changes WHERE operation_id=@Operation;",new{Operation=Guid.ParseExact(change.OperationId,"N")},tx,cancellationToken:ct));
  if(prior is not null){var saved=Parse(prior);if(saved.RecordedBy!=actor||saved.CustomerId!=customer||saved.Before.Version!=change.ExpectedVersion||saved.After.Active!=change.Active||saved.Reason!=change.Reason)throw new TenantBoundaryException("CUSTOMER_OPERATION_CONFLICT",409);await tx.CommitAsync(ct);return saved;}
  var before=await Read(db,tx,tenant,customer,ct)??throw new TenantBoundaryException("CUSTOMER_NOT_FOUND",404);
  if(before.Version!=change.ExpectedVersion)throw new TenantBoundaryException("CUSTOMER_VERSION_CONFLICT",409);
  if(before.Active==change.Active)throw new TenantBoundaryException("CUSTOMER_STATUS_UNCHANGED",409);
  if(change.Active&&!before.ActivationAvailable)throw new TenantBoundaryException("CUSTOMER_ACTIVATION_UNAVAILABLE",409);
  await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(owner_actor_id,92)) FROM customer_identity.customers WHERE customer_id=@Id AND owner_actor_id IS NOT NULL;",new{Id=customer},tx,cancellationToken:ct));
  if(change.Active&&before.OwnerLinked){var overlap=await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM customer_identity.current_customers WHERE active AND customer_id<>@Id AND owner_actor_id=(SELECT owner_actor_id FROM customer_identity.customers WHERE customer_id=@Id));",new{Id=customer},tx,cancellationToken:ct));if(overlap)throw new TenantBoundaryException("CUSTOMER_OWNER_AMBIGUOUS",409);}
  await db.ExecuteAsync(new CommandDefinition("INSERT INTO customer_identity.customer_availability(customer_id,active,version) VALUES(@Id,@Active,1) ON CONFLICT(customer_id) DO UPDATE SET active=EXCLUDED.active,version=customer_identity.customer_availability.version+1;",new{Id=customer,change.Active},tx,cancellationToken:ct));
  var after=await Read(db,tx,tenant,customer,ct)??throw new TenantBoundaryException("CUSTOMER_NOT_FOUND",404);var label=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT normalized_email FROM customer_identity.tenant_principals WHERE actor_id=@Actor;",new{Actor=actor},tx,cancellationToken:ct))??throw new TenantBoundaryException("ADMINISTRATOR_IDENTITY_UNAVAILABLE",503);var receipt=new CustomerStatusReceipt(tenant,customer,change.OperationId,before,after,change.Reason,actor,label,now);
  await db.ExecuteAsync(new CommandDefinition("INSERT INTO customer_identity.customer_status_changes(operation_id,customer_id,actor_id,customer_version,payload,recorded_at) VALUES(@Operation,@Id,@Actor,@Version,CAST(@Payload AS jsonb),@Now);",new{Operation=Guid.ParseExact(change.OperationId,"N"),Id=customer,Actor=actor,Version=after.Version,Payload=JsonSerializer.Serialize(receipt,Json),Now=now},tx,cancellationToken:ct));
  await tx.CommitAsync(ct);return receipt;
 }
 public async Task<CustomerStatusReceipt?> ReceiptAsync(string tenant,string customer,string operation,CancellationToken ct)
 {await using var db=await sessions.OpenAsync(tenant,ct);var row=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.customer_status_changes WHERE customer_id=@Id AND operation_id=@Operation;",new{Id=customer,Operation=Guid.ParseExact(operation,"N")},cancellationToken:ct));return row is null?null:Parse(row);}
 public async Task<CustomerStatusHistory> HistoryAsync(string tenant,string customer,long? before,CancellationToken ct)
 {await using var db=await sessions.OpenAsync(tenant,ct);if(await Read(db,null,tenant,customer,ct)is null)throw new TenantBoundaryException("CUSTOMER_NOT_FOUND",404);var rows=(await db.QueryAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.customer_status_changes WHERE customer_id=@Id AND (@Before=0 OR customer_version<@Before) ORDER BY customer_version DESC LIMIT 51;",new{Id=customer,Before=before??0},cancellationToken:ct))).Select(Parse).ToArray();return new(tenant,customer,rows.Take(50).ToArray(),rows.Length>50?rows[49].After.Version:null);}
 private static CustomerStatusReceipt Parse(string value)=>JsonSerializer.Deserialize<CustomerStatusReceipt>(value,Json)??throw new TenantBoundaryException("CUSTOMER_RECEIPT_INVALID",503);
 private sealed record Counts(long Total,long Active,long Inactive);
}
