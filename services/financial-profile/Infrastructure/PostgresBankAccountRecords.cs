using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Npgsql;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class PostgresBankAccountRecords : IBankAccountRecordRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public PostgresBankAccountRecords(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        source = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:FinancialProfile:RuntimeConnection"));
    }

    public async Task<ReviewedBankAccountRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedBankAccountRecord record, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, tx, tenant, customer, actor, record.OperationId, record.BankAccountRecordId, ct).ConfigureAwait(false);
        if (await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM financial_profile.bank_account_record_revisions
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation);
            """, new { Tenant=tenant, Customer=customer, Actor=actor, Operation=record.OperationId }, tx,
            cancellationToken:ct)).ConfigureAwait(false)) Conflict();
        var prior = await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("""
            SELECT fingerprint AS Fingerprint,payload::text AS Payload
            FROM financial_profile.bank_account_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@Id;
            """, new { Tenant=tenant, Customer=customer, Id=record.BankAccountRecordId }, tx,
            cancellationToken:ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            var replay = Decode(prior.Payload);
            if (prior.Fingerprint != fingerprint || replay.RecordedBy != actor) Conflict();
            await tx.CommitAsync(ct).ConfigureAwait(false); return replay;
        }
        var inserted = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.bank_account_records
              (tenant_id,customer_id,bank_account_record_id,operation_id,actor_id,fingerprint,
               source_financial_fact_id,source_revision,payload,recorded_at)
            SELECT @Tenant,@Customer,@BankAccountRecordId,@OperationId,@Actor,@Fingerprint,
                   f.financial_fact_id,f.revision,CAST(@Payload AS jsonb),@RecordedAt
            FROM financial_profile.fact_revisions f
            WHERE f.financial_fact_id=@Source AND f.revision=@SourceRevision
              AND f.customer_id=@Customer AND f.fact_type='BANK_ACCOUNT';
            """, new { Tenant=tenant, Customer=customer, record.BankAccountRecordId, record.OperationId,
                Actor=actor, Fingerprint=fingerprint, Source=record.Account.SourceFinancialFactId,
                SourceRevision=record.Account.SourceRevision,
                Payload=JsonSerializer.Serialize(record,ContractJson.Options), record.RecordedAt }, tx,
            cancellationToken:ct)).ConfigureAwait(false);
        if (inserted != 1) throw new TenantBoundaryException("BANK_ACCOUNT_SOURCE_NOT_FOUND",404);
        await tx.CommitAsync(ct).ConfigureAwait(false); return record;
    }

    public async Task<ReviewedBankAccountRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedBankAccountRecord record, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db,tx,tenant,customer,actor,record.OperationId,record.BankAccountRecordId,ct).ConfigureAwait(false);
        var args = new { Tenant=tenant,Customer=customer,Actor=actor,Operation=record.OperationId,Id=record.BankAccountRecordId };
        var prior = await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("""
            SELECT fingerprint AS Fingerprint,payload::text AS Payload
            FROM financial_profile.bank_account_record_revisions
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation;
            """,args,tx,cancellationToken:ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            var replay=Decode(prior.Payload);
            if(prior.Fingerprint!=fingerprint || replay.BankAccountRecordId!=record.BankAccountRecordId) Conflict();
            await tx.CommitAsync(ct).ConfigureAwait(false); return replay;
        }
        if(await db.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM financial_profile.bank_account_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND actor_id=@Actor AND operation_id=@Operation);
            """,args,tx,cancellationToken:ct)).ConfigureAwait(false)) Conflict();
        var current=await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT COALESCE((SELECT MAX(r.revision) FROM financial_profile.bank_account_record_revisions r
              WHERE r.tenant_id=l.tenant_id AND r.customer_id=l.customer_id
              AND r.bank_account_record_id=l.bank_account_record_id),1)
            FROM financial_profile.bank_account_records l
            WHERE l.tenant_id=@Tenant AND l.customer_id=@Customer AND l.bank_account_record_id=@Id;
            """,args,tx,cancellationToken:ct)).ConfigureAwait(false);
        if(current is null) NotFound();
        if(current!=expectedRevision) throw new TenantBoundaryException("BANK_ACCOUNT_REVISION_CONFLICT",409);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.bank_account_record_revisions
              (tenant_id,customer_id,bank_account_record_id,revision,operation_id,actor_id,fingerprint,payload,recorded_at)
            VALUES (@Tenant,@Customer,@BankAccountRecordId,@Revision,@OperationId,@Actor,@Fingerprint,CAST(@Payload AS jsonb),@RecordedAt);
            """,new {Tenant=tenant,Customer=customer,record.BankAccountRecordId,record.Revision,record.OperationId,
                Actor=actor,Fingerprint=fingerprint,Payload=JsonSerializer.Serialize(record,ContractJson.Options),record.RecordedAt},
            tx,cancellationToken:ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false); return record;
    }

    public async Task<ReviewedBankAccountRecord?> ReadAsync(string tenant,string customer,string id,
        CancellationToken ct,int? revision=null)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var payload=await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT payload FROM (
              SELECT 1 AS revision,payload::text AS payload FROM financial_profile.bank_account_records
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@Id
              UNION ALL
              SELECT revision,payload::text FROM financial_profile.bank_account_record_revisions
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@Id
            ) versions WHERE (@Revision IS NULL OR revision=@Revision) ORDER BY revision DESC LIMIT 1;
            """,new {Tenant=tenant,Customer=customer,Id=id,Revision=revision},cancellationToken:ct)).ConfigureAwait(false);
        return payload is null?null:Decode(payload);
    }

    public async Task<BankAccountRecordList> ListAsync(string tenant,string customer,CancellationToken ct,string? afterId=null)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        DateTimeOffset? anchor=afterId is null?null:await db.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT recorded_at FROM financial_profile.bank_account_records WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@After",
            new {Tenant=tenant,Customer=customer,After=afterId},cancellationToken:ct)).ConfigureAwait(false);
        if(afterId is not null && anchor is null) throw new TenantBoundaryException("RECORD_PAGE_NOT_FOUND",404);
        var rows=(await db.QueryAsync<string>(new CommandDefinition("""
            SELECT COALESCE((SELECT r.payload::text FROM financial_profile.bank_account_record_revisions r
              WHERE r.tenant_id=l.tenant_id AND r.customer_id=l.customer_id
              AND r.bank_account_record_id=l.bank_account_record_id ORDER BY r.revision DESC LIMIT 1),l.payload::text)
            FROM financial_profile.bank_account_records l WHERE l.tenant_id=@Tenant AND l.customer_id=@Customer
            AND (@After::text IS NULL OR l.recorded_at<@Anchor OR
              l.recorded_at=@Anchor AND l.bank_account_record_id COLLATE "C">@After COLLATE "C")
            ORDER BY l.recorded_at DESC,l.bank_account_record_id COLLATE "C" LIMIT 201;
            """,new {Tenant=tenant,Customer=customer,After=afterId,Anchor=anchor},cancellationToken:ct)).ConfigureAwait(false)).ToArray();
        return new(rows.Take(200).Select(Decode).ToArray(),rows.Length>200);
    }

    public async Task<BankAccountRecordHistory> HistoryAsync(string tenant,string customer,string id,
        int? beforeRevision,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows=(await db.QueryAsync<string>(new CommandDefinition("""
            SELECT payload FROM (
              SELECT 1 AS revision,payload::text AS payload FROM financial_profile.bank_account_records
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@Id
              UNION ALL
              SELECT revision,payload::text FROM financial_profile.bank_account_record_revisions
              WHERE tenant_id=@Tenant AND customer_id=@Customer AND bank_account_record_id=@Id
            ) versions WHERE (@Before IS NULL OR revision<@Before) ORDER BY revision DESC LIMIT 51;
            """,new {Tenant=tenant,Customer=customer,Id=id,Before=beforeRevision},cancellationToken:ct)).ConfigureAwait(false)).ToArray();
        var items=rows.Take(50).Select(Decode).ToArray();
        return new(items,rows.Length>50,rows.Length>50?items[^1].Revision:null);
    }

    private static async Task LockAsync(NpgsqlConnection db,NpgsqlTransaction tx,string tenant,string customer,
        string actor,string operation,string id,CancellationToken ct)
    {
        foreach(var key in new[]{JsonSerializer.Serialize(new[]{"bank-account-operation",tenant,customer,actor,operation}),
            JsonSerializer.Serialize(new[]{"bank-account-record",tenant,customer,id})})
            await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Key,0));",
                new{Key=key},tx,cancellationToken:ct)).ConfigureAwait(false);
    }
    private static ReviewedBankAccountRecord Decode(string value)=>JsonSerializer.Deserialize<ReviewedBankAccountRecord>(value,ContractJson.Options)
        ?? throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_UNAVAILABLE",503);
    private static void Conflict()=>throw new TenantBoundaryException("BANK_ACCOUNT_OPERATION_CONFLICT",409);
    private static void NotFound()=>throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_NOT_FOUND",404);
    private sealed record Stored(string Fingerprint,string Payload);
    public ValueTask DisposeAsync()=>source.DisposeAsync();
}
