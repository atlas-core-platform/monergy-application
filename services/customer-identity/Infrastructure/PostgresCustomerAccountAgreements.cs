using System.Text.Json;
using Dapper;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Npgsql;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomerAccountAgreements(PostgresTenantSessions sessions) : ICustomerAccountAgreementRepository
{
    public async Task<AccountAgreementReceipt?> ReadAsync(string tenant, string actor, string customer, string revision, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT operation_id AS OperationId,revision AS Revision,accepted_at AS AcceptedAt
            FROM customer_identity.account_agreement_acceptances
            WHERE actor_id=@Actor AND customer_id=@Customer AND revision=@Revision;
            """, new { Actor = actor, Customer = customer, Revision = revision }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToReceipt();
    }
    public async Task<AccountAgreementReceipt> AcceptAsync(string tenant, string actor, string customer, AccountAgreementPublication publication,
        string operationId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await OpenAsync(tenant, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@Customer,53));",
            new { Customer = customer }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        async Task ConfirmOwnerAsync()
        {
            var owned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM customer_identity.current_customers WHERE customer_id=@Customer AND owner_actor_id=@Actor AND active)
                    AND (SELECT count(*) FROM customer_identity.current_customers WHERE owner_actor_id=@Actor AND active)=1;
                """, new { Customer = customer, Actor = actor }, transaction, cancellationToken: ct)).ConfigureAwait(false);
            if (!owned) throw new TenantAccessException("OWNED_CUSTOMER_REQUIRED", 403);
        }
        await ConfirmOwnerAsync().ConfigureAwait(false);
        var operation = Guid.ParseExact(operationId, "N");
        var prior = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT operation_id AS OperationId,customer_id AS CustomerId,actor_id AS ActorId,revision AS Revision,accepted_at AS AcceptedAt
            FROM customer_identity.account_agreement_acceptances WHERE operation_id=@Operation;
            """, new { Operation = operation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.CustomerId != customer || prior.ActorId != actor || prior.Revision != publication.Revision)
                throw new TenantAccessException("AGREEMENTS_OPERATION_CONFLICT", 409);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return prior.ToReceipt();
        }
        var snapshot = JsonSerializer.Serialize(publication);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.account_agreement_publications(revision,content_json,published_at)
            VALUES (@Revision,@Snapshot,@PublishedAt) ON CONFLICT DO NOTHING;
            """, new { publication.Revision, Snapshot = snapshot, publication.PublishedAt }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        var stored = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT content_json FROM customer_identity.account_agreement_publications WHERE revision=@Revision;",
            new { publication.Revision }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (stored != snapshot) throw new TenantAccessException("AGREEMENTS_PUBLICATION_INVALID", 503);
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.account_agreement_acceptances(operation_id,customer_id,actor_id,revision,
                terms_accepted,privacy_acknowledged,processing_consented,accepted_at)
            VALUES (@Operation,@Customer,@Actor,@Revision,true,true,true,@Now) ON CONFLICT DO NOTHING;
            """, new { Operation = operation, Customer = customer, Actor = actor, publication.Revision, Now = now }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (inserted != 1) throw new TenantAccessException("AGREEMENTS_ACCEPTANCE_CONFLICT", 409);
        await ConfirmOwnerAsync().ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(operationId, publication.Revision, now);
    }
    private async Task<NpgsqlConnection> OpenAsync(string tenant, CancellationToken ct)
    {
        var connection = await sessions.OpenAsync(tenant, ct).ConfigureAwait(false);
        try
        {
            var safe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT NOT has_table_privilege(current_user,'customer_identity.account_agreement_publications','UPDATE,DELETE,TRUNCATE')
                    AND NOT has_table_privilege(current_user,'customer_identity.account_agreement_acceptances','UPDATE,DELETE,TRUNCATE');
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!safe) throw new TenantAccessException("AGREEMENTS_BINDING_INVALID", 503);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class ReceiptRow
    {
        public Guid OperationId { get; set; }
        public string CustomerId { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string Revision { get; set; } = "";
        public DateTime AcceptedAt { get; set; }
        public AccountAgreementReceipt ToReceipt() => new(OperationId.ToString("N"), Revision, new(DateTime.SpecifyKind(AcceptedAt, DateTimeKind.Utc)));
    }
}
