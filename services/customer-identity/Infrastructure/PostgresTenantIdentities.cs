using Dapper;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

// Uses the same validated C&I tenant connection boundary as sessions. The
// transaction covers identity creation and its durable receipt, never AM SQL.
public sealed class PostgresTenantIdentities(PostgresTenantSessions connections)
{
    public async Task<TenantIdentityProvisioningReceipt> ProvisionAsync(string trustedTenant,
        TenantIdentityProvisioningRequest request, CancellationToken cancellationToken)
    {
        TenantIdentityProvisioningProtocol.Validate(request, trustedTenant);
        await using var connection = await connections.OpenAsync(trustedTenant, cancellationToken).ConfigureAwait(false);
        var allowed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.provisioning_schema WHERE singleton AND version=1)
                AND NOT has_table_privilege(current_user,'customer_identity.provisioning_schema','INSERT,UPDATE,DELETE,TRUNCATE')
                AND NOT has_table_privilege(current_user,'customer_identity.tenant_principals','UPDATE,DELETE,TRUNCATE')
                AND NOT has_table_privilege(current_user,'customer_identity.provisioning_receipts','UPDATE,DELETE,TRUNCATE');
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!allowed) throw new TenantAccessException("IDENTITY_BINDING_INVALID", 503);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Serializes this bounded tenant provisioning operation. Email and key
        // uniqueness remain database constraints, including across restarts.
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(705006);",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var prior = await connection.QuerySingleOrDefaultAsync<ReceiptRow>(new CommandDefinition("""
            SELECT import_id AS ImportId,row_number AS RowNumber,normalized_email AS NormalizedEmail,
                actor_id AS ActorId,idempotency_key AS IdempotencyKey
            FROM customer_identity.provisioning_receipts WHERE idempotency_key=@IdempotencyKey;
            """, request, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.ImportId != request.ImportId || prior.RowNumber != request.RowNumber || prior.NormalizedEmail != request.NormalizedEmail)
                throw new TenantAccessException("IDENTITY_IDEMPOTENCY_CONFLICT");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(trustedTenant, prior.NormalizedEmail, prior.ActorId, prior.IdempotencyKey);
        }
        var occupied = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM customer_identity.provisioning_receipts WHERE import_id=@ImportId AND row_number=@RowNumber);
            """, request, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (occupied) throw new TenantAccessException("IDENTITY_IMPORT_ROW_CONFLICT");
        var actor = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT actor_id FROM customer_identity.tenant_principals WHERE normalized_email=@NormalizedEmail;
            """, request, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (actor is null)
        {
            actor = "ci-" + Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO customer_identity.tenant_principals(actor_id,normalized_email) VALUES (@ActorId,@NormalizedEmail);
                """, new { ActorId = actor, request.NormalizedEmail }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO customer_identity.provisioning_receipts(idempotency_key,import_id,row_number,normalized_email,actor_id)
            VALUES (@IdempotencyKey,@ImportId,@RowNumber,@NormalizedEmail,@ActorId);
            """, new { request.IdempotencyKey, request.ImportId, request.RowNumber, request.NormalizedEmail, ActorId = actor },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(trustedTenant, request.NormalizedEmail, actor, request.IdempotencyKey);
    }

    private sealed class ReceiptRow
    {
        public string ImportId { get; set; } = "";
        public int RowNumber { get; set; }
        public string NormalizedEmail { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
    }
}
