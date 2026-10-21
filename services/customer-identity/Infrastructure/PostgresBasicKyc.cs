using Monergy.Contracts;
using System.Text.Json;
using Dapper;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresBasicKyc(PostgresTenantSessions sessions) : IBasicKycRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<KycCase?> ReadAsync(string tenant, string customer, CancellationToken ct)
    {
        await using var db = await sessions.OpenAsync(tenant, ct);
        var data = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.kyc_events WHERE customer_id=@customer ORDER BY version DESC LIMIT 1", new { customer }, cancellationToken: ct));
        return data is null ? null : JsonSerializer.Deserialize<KycCase>(data, Json);
    }
    public async Task<KycCase[]> HistoryAsync(string tenant, string customer, long before, CancellationToken ct)
    {
        await using var db = await sessions.OpenAsync(tenant, ct);
        var data = await db.QueryAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.kyc_events WHERE customer_id=@customer AND version<@before ORDER BY version DESC LIMIT 51", new { customer, before }, cancellationToken: ct));
        return data.Select(x => JsonSerializer.Deserialize<KycCase>(x, Json)!).ToArray();
    }
    public async Task<KycCase[]> QueueAsync(string tenant, string reviewer, string after, CancellationToken ct)
    {
        await using var db = await sessions.OpenAsync(tenant, ct);
        var data = await db.QueryAsync<string>(new CommandDefinition("""
            SELECT payload::text FROM (SELECT DISTINCT ON(customer_id) customer_id,payload FROM customer_identity.kyc_events ORDER BY customer_id,version DESC) latest
            WHERE customer_id>@after AND payload->>'reviewerId'=@reviewer AND payload->>'state'='SUBMITTED'
                AND EXISTS(SELECT 1 FROM customer_identity.current_customers c WHERE c.customer_id=latest.customer_id AND c.owner_actor_id=payload->>'ownerActorId' AND c.active)
            ORDER BY customer_id LIMIT 51
            """, new { reviewer, after }, cancellationToken: ct));
        return data.Select(x => JsonSerializer.Deserialize<KycCase>(x, Json)!).ToArray();
    }
    public async Task<KycCase> ChangeAsync(string tenant, string actor, string customer, KycChange change, Func<KycCase?, KycCase> transition, CancellationToken ct)
    {
        await using var db = await sessions.OpenAsync(tenant, ct);
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT has_table_privilege(current_user,'customer_identity.kyc_events','UPDATE,DELETE,TRUNCATE')", cancellationToken: ct))) throw new TenantAccessException("KYC_STORAGE_GRANTS_INVALID", 503);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@customer,103))", new { customer }, tx, cancellationToken: ct));
        var operation = Guid.ParseExact(change.OperationId, "N");
        var input = JsonSerializer.Serialize(new { actor, customer, change }, Json);
        var prior = await db.QuerySingleOrDefaultAsync<EventRow>(new CommandDefinition("SELECT input::text AS Input,payload::text AS Payload FROM customer_identity.kyc_events WHERE operation_id=@operation", new { operation }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(input), System.Text.Json.Nodes.JsonNode.Parse(prior.Input))) throw new TenantAccessException("KYC_OPERATION_CONFLICT", 409);
            return JsonSerializer.Deserialize<KycCase>(prior.Payload, Json)!;
        }
        var data = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload::text FROM customer_identity.kyc_events WHERE customer_id=@customer ORDER BY version DESC LIMIT 1", new { customer }, tx, cancellationToken: ct));
        var next = transition(data is null ? null : JsonSerializer.Deserialize<KycCase>(data, Json));
        var owner = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT owner_actor_id FROM customer_identity.current_customers WHERE customer_id=@customer AND active", new { customer }, tx, cancellationToken: ct));
        if (owner != next.OwnerActorId) throw new TenantAccessException("KYC_CUSTOMER_CHANGED", 409);
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO customer_identity.kyc_events(operation_id,customer_id,version,input,payload) VALUES(@operation,@customer,@Version,CAST(@input AS jsonb),CAST(@payload AS jsonb))", new { operation, customer, next.Version, input, payload = JsonSerializer.Serialize(next, Json) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return next;
    }
    private sealed class EventRow { public string Input { get; set; } = ""; public string Payload { get; set; } = ""; }
}
