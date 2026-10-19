using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Npgsql;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class PostgresLiabilityRecords : ILiabilityRecordRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public PostgresLiabilityRecords(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        source = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:FinancialProfile:RuntimeConnection"));
    }
    public async Task<ReviewedLiabilityRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedLiabilityRecord record, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, tx, tenant, customer, actor, record.OperationId, record.LiabilityRecordId, ct).ConfigureAwait(false);
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM financial_profile.liability_record_revisions
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation);
            """, new { Tenant = tenant, Customer = customer, Actor = actor, Operation = record.OperationId }, tx, cancellationToken: ct)).ConfigureAwait(false))
            throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
        var prior = await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("""
            SELECT fingerprint AS Fingerprint, payload::text AS Payload FROM financial_profile.liability_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND liability_record_id=@Id;
            """, new { Tenant = tenant, Customer = customer, Id = record.LiabilityRecordId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint) throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
            var replay = Decode(prior.Payload);
            if (replay.RecordedBy != actor) throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return replay;
        }
        // Append-only record is also the immutable creation receipt: actor, operation, exact source and timestamp.
        var inserted = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.liability_records
              (tenant_id,customer_id,liability_record_id,operation_id,actor_id,fingerprint,
               source_financial_fact_id,source_revision,payload,recorded_at)
            SELECT @Tenant,@Customer,@LiabilityRecordId,@OperationId,@Actor,@Fingerprint,
                   f.financial_fact_id,f.revision,CAST(@Payload AS jsonb),@RecordedAt
            FROM financial_profile.fact_revisions f
            WHERE f.financial_fact_id=@Source AND f.revision=@Revision AND f.customer_id=@Customer AND f.fact_type='LOAN';
            """, new { Tenant = tenant, Customer = customer, record.LiabilityRecordId, record.OperationId, Actor = actor,
                Fingerprint = fingerprint, Source = record.Liability.SourceFinancialFactId, Revision = record.Liability.SourceRevision,
                Payload = JsonSerializer.Serialize(record, ContractJson.Options), record.RecordedAt }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (inserted != 1) throw new TenantBoundaryException("LIABILITY_SOURCE_NOT_FOUND", 404);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return record;
    }
    public async Task<ReviewedLiabilityRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedLiabilityRecord record, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, tx, tenant, customer, actor, record.OperationId, record.LiabilityRecordId, ct).ConfigureAwait(false);
        var args = new { Tenant = tenant, Customer = customer, Actor = actor, Operation = record.OperationId, Id = record.LiabilityRecordId };
        var prior = await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("""
            SELECT fingerprint AS Fingerprint,payload::text AS Payload FROM financial_profile.liability_record_revisions
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation;
            """, args, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            var replay = Decode(prior.Payload);
            if (prior.Fingerprint != fingerprint || replay.LiabilityRecordId != record.LiabilityRecordId)
                throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return replay;
        }
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM financial_profile.liability_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation);
            """, args, tx, cancellationToken: ct)).ConfigureAwait(false))
            throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
        var current = await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT COALESCE((SELECT MAX(r.revision) FROM financial_profile.liability_record_revisions r
              WHERE r.tenant_id=l.tenant_id AND r.customer_id=l.customer_id AND r.liability_record_id=l.liability_record_id),1)
            FROM financial_profile.liability_records l WHERE l.tenant_id=@Tenant AND l.customer_id=@Customer AND l.liability_record_id=@Id;
            """, args, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (current is null) throw new TenantBoundaryException("LIABILITY_RECORD_NOT_FOUND", 404);
        if (current != expectedRevision) throw new TenantBoundaryException("LIABILITY_REVISION_CONFLICT", 409);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.liability_record_revisions
              (tenant_id,customer_id,liability_record_id,revision,operation_id,actor_id,fingerprint,payload,recorded_at)
            VALUES (@Tenant,@Customer,@LiabilityRecordId,@Revision,@OperationId,@Actor,@Fingerprint,CAST(@Payload AS jsonb),@RecordedAt);
            """, new { Tenant = tenant, Customer = customer, record.LiabilityRecordId, record.Revision, record.OperationId, Actor = actor,
                Fingerprint = fingerprint, Payload = JsonSerializer.Serialize(record, ContractJson.Options), record.RecordedAt }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return record;
    }
    public async Task<ReviewedLiabilityRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var payload = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT payload FROM (
              SELECT 1 AS revision,payload::text AS payload FROM financial_profile.liability_records
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND liability_record_id=@Id
              UNION ALL
              SELECT revision,payload::text FROM financial_profile.liability_record_revisions
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND liability_record_id=@Id
            ) versions WHERE (@Revision IS NULL OR revision=@Revision) ORDER BY revision DESC LIMIT 1;
            """, new { Tenant = tenant, Customer = customer, Id = id, Revision = revision }, cancellationToken: ct)).ConfigureAwait(false);
        return payload is null ? null : Decode(payload);
    }
    public async Task<LiabilityRecordList> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT COALESCE((SELECT r.payload::text FROM financial_profile.liability_record_revisions r
              WHERE r.tenant_id=l.tenant_id AND r.customer_id=l.customer_id AND r.liability_record_id=l.liability_record_id
              ORDER BY r.revision DESC LIMIT 1),l.payload::text)
            FROM financial_profile.liability_records l WHERE l.tenant_id=@Tenant AND l.customer_id=@Customer
            ORDER BY l.recorded_at DESC,l.liability_record_id LIMIT 201;
            """, new { Tenant = tenant, Customer = customer }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        return new(rows.Take(200).Select(Decode).ToArray(), rows.Length > 200);
    }
    public async Task<LiabilityRecordSnapshot> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows=(await db.QueryAsync<string>(new CommandDefinition("""
            WITH latest AS (
              SELECT l.recorded_at,l.liability_record_id,COALESCE((SELECT r.payload::text
                FROM financial_profile.liability_record_revisions r WHERE r.tenant_id=l.tenant_id
                AND r.customer_id=l.customer_id AND r.liability_record_id=l.liability_record_id
                ORDER BY r.revision DESC LIMIT 1),l.payload::text) AS payload
              FROM financial_profile.liability_records l WHERE l.tenant_id=@Tenant AND l.customer_id=@Customer
            )
            SELECT payload FROM latest WHERE (CAST(payload AS jsonb)->>'currency')=@Currency
              AND (CAST(payload AS jsonb)#>>'{liability,balanceAsOfDate}')=@AsOfDate
            ORDER BY recorded_at DESC,liability_record_id LIMIT 201;
            """,new {Tenant=tenant,Customer=customer,Currency=currency,AsOfDate=asOfDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)},cancellationToken:ct)).ConfigureAwait(false)).ToArray();
        return new(asOfDate,currency,rows.Take(200).Select(Decode).ToArray(),rows.Length>200);
    }
    public async Task<LiabilityRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT payload FROM (
              SELECT 1 AS revision,payload::text AS payload FROM financial_profile.liability_records
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND liability_record_id=@Id
              UNION ALL
              SELECT revision,payload::text FROM financial_profile.liability_record_revisions
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND liability_record_id=@Id
            ) versions WHERE (@Before IS NULL OR revision<@Before) ORDER BY revision DESC LIMIT 51;
            """, new { Tenant = tenant, Customer = customer, Id = id, Before = beforeRevision }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        var items = rows.Take(50).Select(Decode).ToArray();
        return new(items, rows.Length > 50, rows.Length > 50 ? items[^1].Revision : null);
    }
    private static async Task LockAsync(NpgsqlConnection db, NpgsqlTransaction tx, string tenant, string customer,
        string actor, string operation, string id, CancellationToken ct)
    {
        foreach (var key in new[] { JsonSerializer.Serialize(new[] { "liability-operation", tenant, customer, actor, operation }),
            JsonSerializer.Serialize(new[] { "liability-record", tenant, customer, id }) })
            await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));",
                new { Key = key }, tx, cancellationToken: ct)).ConfigureAwait(false);
    }
    private static ReviewedLiabilityRecord Decode(string value) => JsonSerializer.Deserialize<ReviewedLiabilityRecord>(value, ContractJson.Options)
        ?? throw new TenantBoundaryException("LIABILITY_RECORD_UNAVAILABLE", 503);
    private sealed record Stored(string Fingerprint, string Payload);
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
