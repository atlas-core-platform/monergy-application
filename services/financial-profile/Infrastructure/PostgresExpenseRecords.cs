using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Npgsql;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class PostgresExpenseRecords : IExpenseRecordRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    public PostgresExpenseRecords(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        source = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration, "Monergy:Persistence:FinancialProfile:RuntimeConnection"));
    }
    public async Task<ReviewedExpenseRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedExpenseRecord record, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Id, 0));",
            new { Id = record.ExpenseRecordId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var prior = await db.QuerySingleOrDefaultAsync<Stored>(new CommandDefinition("""
            SELECT fingerprint AS Fingerprint, payload::text AS Payload FROM financial_profile.expense_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND expense_record_id=@Id;
            """, new { Tenant = tenant, Customer = customer, Id = record.ExpenseRecordId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint) throw new TenantBoundaryException("EXPENSE_OPERATION_CONFLICT", 409);
            var replay = Decode(prior.Payload);
            if (replay.RecordedBy != actor) throw new TenantBoundaryException("EXPENSE_OPERATION_CONFLICT", 409);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return replay;
        }
        // Append-only record is also the immutable creation receipt: actor, operation, exact source and timestamp.
        var inserted = await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO financial_profile.expense_records
              (tenant_id,customer_id,expense_record_id,operation_id,actor_id,fingerprint,
               source_financial_fact_id,source_revision,payload,recorded_at)
            SELECT @Tenant,@Customer,@ExpenseRecordId,@OperationId,@Actor,@Fingerprint,
                   f.financial_fact_id,f.revision,CAST(@Payload AS jsonb),@RecordedAt
            FROM financial_profile.fact_revisions f
            WHERE f.financial_fact_id=@Source AND f.revision=@Revision AND f.customer_id=@Customer AND f.fact_type='EXPENSE';
            """, new { Tenant = tenant, Customer = customer, record.ExpenseRecordId, record.OperationId, Actor = actor,
                Fingerprint = fingerprint, Source = record.Expense.SourceFinancialFactId, Revision = record.Expense.SourceRevision,
                Payload = JsonSerializer.Serialize(record, ContractJson.Options), record.RecordedAt }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (inserted != 1) throw new TenantBoundaryException("EXPENSE_SOURCE_NOT_FOUND", 404);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return record;
    }
    public async Task<ReviewedExpenseRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var payload = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT payload::text FROM financial_profile.expense_records
            WHERE tenant_id=@Tenant AND customer_id=@Customer AND expense_record_id=@Id;
            """, new { Tenant = tenant, Customer = customer, Id = id }, cancellationToken: ct)).ConfigureAwait(false);
        return payload is null ? null : Decode(payload);
    }
    public async Task<ExpenseRecordList> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = (await db.QueryAsync<string>(new CommandDefinition("""
            SELECT payload::text FROM financial_profile.expense_records WHERE tenant_id=@Tenant AND customer_id=@Customer
            ORDER BY recorded_at DESC,expense_record_id LIMIT 201;
            """, new { Tenant = tenant, Customer = customer }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        return new(rows.Take(200).Select(Decode).ToArray(), rows.Length > 200);
    }
    private static ReviewedExpenseRecord Decode(string value) => JsonSerializer.Deserialize<ReviewedExpenseRecord>(value, ContractJson.Options)
        ?? throw new TenantBoundaryException("EXPENSE_RECORD_UNAVAILABLE", 503);
    private sealed record Stored(string Fingerprint, string Payload);
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
