using Dapper;

namespace Monergy.AccessManagement;

public sealed record CustomerRelationshipWrite(long ExpectedPolicyVersion, string ActorId, string CustomerId);

public sealed partial class PostgresAccessAdministration
{
    public Task<AdministrationResult> AssignCustomerAsync(TrustedActorContext context, CustomerRelationshipWrite request,
        ICustomerContextClient customers, CancellationToken ct)
    {
        AccessInput.Identifier(request.ActorId); AccessInput.Identifier(request.CustomerId, 128);
        var id = Guid.NewGuid().ToString("N");
        return MutateAsync(context, request.ExpectedPolicyVersion, id, "customer-relationship.created", async (connection, transaction) =>
        {
            var customer = await customers.ReadAsync(context.TenantId, request.CustomerId, ct).ConfigureAwait(false);
            AccessInput.Require(customer is { Active: true, Version: > 0 } && customer.TenantId == context.TenantId &&
                customer.CustomerId == request.CustomerId, "CUSTOMER_NOT_FOUND", 404);
            AccessInput.Require(customer!.OwnerActorId != request.ActorId, "SELF_RELATIONSHIP_NOT_REQUIRED", 409);
            var capabilities = catalog.All.Where(CustomerAccessPolicy.Supports).Select(item => item.CapabilityId).ToArray();
            var eligible = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS(SELECT 1 FROM am.access_subjects s JOIN am.role_permissions rp ON rp.role_id=s.business_role_id
                    JOIN am.permission_capabilities pc ON pc.permission_id=rp.permission_id
                    WHERE s.actor_id=@ActorId AND s.status='Active' AND pc.capability_id=ANY(@Capabilities));
                """, new { request.ActorId, Capabilities = capabilities }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(eligible, "CUSTOMER_CAPABILITY_NOT_ASSIGNED", 409);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.customer_relationships(relationship_id,actor_id,customer_id,assigned_by)
                VALUES(@Id,@ActorId,@CustomerId,@AssignedBy);
                """, new { Id = id, request.ActorId, request.CustomerId, AssignedBy = context.ActorId }, transaction, ct)).ConfigureAwait(false);
            return new MutationEffect(await IncrementSubjectAsync(connection, transaction, request.ActorId, ct).ConfigureAwait(false), request.ActorId);
        }, ct);
    }
}
