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
            SELECT c.customer_id AS ResourceId,coalesce(p.display_name,c.display_name) AS DisplayName,c.secondary_label AS SecondaryLabel
            FROM customer_identity.customers c LEFT JOIN customer_identity.customer_profiles p ON p.customer_id=c.customer_id
            WHERE c.active ORDER BY coalesce(p.display_name,c.display_name),c.customer_id LIMIT 1000;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
    }

    public async Task<CustomerWorkspaceContext?> ReadOwnedAsync(
        string tenantId,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!TenantAccessClient.Identifier(actorId, 128))
            throw new TenantBoundaryException("INVALID_ACTOR", 400);
        await using var connection = await sessions.OpenAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var matches = (await connection.QueryAsync<CustomerWorkspaceContext>(new CommandDefinition("""
            SELECT @TenantId AS TenantId,c.customer_id AS CustomerId,coalesce(p.display_name,c.display_name) AS DisplayName,
                c.secondary_label AS SecondaryLabel,c.owner_actor_id AS OwnerActorId,c.version AS Version,c.active AS Active
            FROM customer_identity.customers c LEFT JOIN customer_identity.customer_profiles p ON p.customer_id=c.customer_id
            WHERE c.owner_actor_id=@ActorId AND c.active
            ORDER BY c.customer_id
            LIMIT 2;
            """, new { TenantId = tenantId, ActorId = actorId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new TenantBoundaryException("CUSTOMER_CONTEXT_AMBIGUOUS", 409),
        };
    }
}
