using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Npgsql;
namespace Monergy.Services.FinancialProfile.Infrastructure;
public sealed class PostgresNetWorthPositions:INetWorthPositionRepository,IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public PostgresNetWorthPositions(IConfiguration config)
    {PhysicalPersistenceGuard.EnsureAllowed(config);source=NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(config,"Monergy:Persistence:FinancialProfile:RuntimeConnection"));}
    public async Task<ReviewedNetWorthPosition> CreateAsync(string tenant,string customer,string actor,string fingerprint,ReviewedNetWorthPosition value,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);await using var tx=await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var args=new {Tenant=tenant,Customer=customer,Id=value.PositionId,Actor=actor,value.OperationId,Fingerprint=fingerprint,Payload=JsonSerializer.Serialize(value,ContractJson.Options),value.RecordedAt};
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Id,0));",new {Id=JsonSerializer.Serialize(new[]{tenant,customer,actor,value.OperationId})},tx,cancellationToken:ct)).ConfigureAwait(false);
        var prior=await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("SELECT fingerprint AS Fingerprint,payload::text AS Payload FROM financial_profile.net_worth_positions WHERE tenant_id=@Tenant AND customer_id=@Customer AND position_id=@Id;",args,tx,cancellationToken:ct)).ConfigureAwait(false);
        if(prior is not null)
        {if(prior.Fingerprint!=fingerprint||Decode(prior.Payload).RecordedBy!=actor)throw new TenantBoundaryException("NET_WORTH_OPERATION_CONFLICT",409);await tx.CommitAsync(ct).ConfigureAwait(false);return Decode(prior.Payload);}
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO financial_profile.net_worth_positions(tenant_id,customer_id,position_id,actor_id,operation_id,fingerprint,payload,recorded_at) VALUES(@Tenant,@Customer,@Id,@Actor,@OperationId,@Fingerprint,CAST(@Payload AS jsonb),@RecordedAt);",args,tx,cancellationToken:ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);return value;
    }
    public async Task<ReviewedNetWorthPosition?> ReadAsync(string tenant,string customer,string id,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);var payload=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM financial_profile.net_worth_positions WHERE tenant_id=@Tenant AND customer_id=@Customer AND position_id=@Id;",new {Tenant=tenant,Customer=customer,Id=id},cancellationToken:ct)).ConfigureAwait(false);return payload is null?null:Decode(payload);
    }
    public async Task<NetWorthPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct)
    {
        var before=beforeId is null?null:await ReadAsync(tenant,customer,beforeId,ct).ConfigureAwait(false)??throw new TenantBoundaryException("NET_WORTH_POSITION_NOT_FOUND",404);
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);var rows=(await db.QueryAsync<string>(new CommandDefinition("SELECT payload::text FROM financial_profile.net_worth_positions WHERE tenant_id=@Tenant AND customer_id=@Customer AND (@BeforeAt IS NULL OR (recorded_at,position_id)<(@BeforeAt,@BeforeId)) ORDER BY recorded_at DESC,position_id DESC LIMIT 51;",new {Tenant=tenant,Customer=customer,BeforeAt=(DateTimeOffset?)before?.RecordedAt,BeforeId=beforeId},cancellationToken:ct)).ConfigureAwait(false)).Select(Decode).ToArray();return new(rows.Take(50).ToArray(),rows.Length>50,rows.Length>50?rows[49].PositionId:null);
    }
    private static ReviewedNetWorthPosition Decode(string payload)=>JsonSerializer.Deserialize<ReviewedNetWorthPosition>(payload,ContractJson.Options)??throw new TenantBoundaryException("NET_WORTH_POSITION_UNAVAILABLE",503);
    private sealed record Stored(string Fingerprint,string Payload);
    public ValueTask DisposeAsync()=>source.DisposeAsync();
}
