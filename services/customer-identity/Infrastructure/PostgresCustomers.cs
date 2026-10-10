using Dapper;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class PostgresCustomers(PostgresTenantSessions sessions) : ITenantCustomerResourceDirectory
{
    public async Task<CustomerOwnerContext?> ReadAsync(string tenantId, string customerId, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(customerId, 128)) throw new TenantBoundaryException("INVALID_CUSTOMER", 400);
        await using var connection = await sessions.OpenAsync(tenantId, ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<CustomerOwnerContext>(new CommandDefinition("""
            SELECT @TenantId AS TenantId,customer_id AS CustomerId,owner_actor_id AS OwnerActorId,version AS Version,active AS Active
            FROM customer_identity.customers WHERE customer_id=@CustomerId;
            """, new { TenantId = tenantId, CustomerId = customerId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantCustomerResource>> ListAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await sessions.OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        // Bounded UAT owner directory; production pagination is a separate contract.
        return (await connection.QueryAsync<TenantCustomerResource>(new CommandDefinition("""
            SELECT customer_id AS ResourceId,display_name AS DisplayName,secondary_label AS SecondaryLabel
            FROM customer_identity.customers WHERE active ORDER BY display_name,customer_id LIMIT 1000;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
    }
}
