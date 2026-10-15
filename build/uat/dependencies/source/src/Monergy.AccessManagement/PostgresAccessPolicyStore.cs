using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class PostgresAccessPolicyStore(ITenantConnectionFactory connections) : IAccessPolicyStore, ICustomerRelationshipStore
{
    public ValueTask<bool> IsAssignedAsync(string tenantId, string actorId, string customerId, CancellationToken cancellationToken) =>
        QueryAsync(tenantId, connection => connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM am.customer_relationships WHERE actor_id=@ActorId AND customer_id=@CustomerId);
            """, new { ActorId = actorId, CustomerId = customerId }, cancellationToken: cancellationToken)), cancellationToken);

    public bool IsReady => connections.IsConfigured;

    public ValueTask<SubjectPolicyState?> GetSubjectPolicyStateAsync(string tenantId, string actorId, CancellationToken ct) =>
        QueryAsync<SubjectPolicyState?>(tenantId, async connection =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<SubjectRow>(new CommandDefinition("""
                SELECT s.actor_id AS ActorId, s.normalized_email AS NormalizedEmail, s.status AS Status,
                       s.business_role_id AS BusinessRoleId, s.administrative_authority AS AdministrativeAuthority,
                       s.subject_version AS SubjectVersion, p.policy_version AS PolicyVersion
                FROM am.access_subjects s CROSS JOIN am.policy_state p
                WHERE s.actor_id=@ActorId AND p.singleton;
                """, new { ActorId = actorId }, cancellationToken: ct)).ConfigureAwait(false);
            return row is null ? null : new SubjectPolicyState(
                new AccessSubject(row.ActorId, row.NormalizedEmail, Enum.Parse<MembershipStatus>(row.Status),
                    row.BusinessRoleId, row.AdministrativeAuthority is null ? null :
                        Enum.Parse<AdministrativeAuthority>(row.AdministrativeAuthority), row.SubjectVersion),
                row.PolicyVersion);
        }, ct);

    public ValueTask<RolePolicySnapshot?> GetRolePolicyAsync(string tenantId, string roleId, long policyVersion, CancellationToken ct) =>
        QueryAsync<RolePolicySnapshot?>(tenantId, async connection =>
        {
            var rows = (await connection.QueryAsync<GrantRow>(new CommandDefinition("""
                SELECT DISTINCT r.role_id AS RoleId, p.policy_version AS PolicyVersion,
                       pc.capability_id AS CapabilityId, pc.scope AS Scope
                FROM am.roles r CROSS JOIN am.policy_state p
                LEFT JOIN am.role_permissions rp ON rp.role_id=r.role_id
                LEFT JOIN am.permission_capabilities pc ON pc.permission_id=rp.permission_id
                WHERE r.role_id=@RoleId AND p.singleton AND p.policy_version=@PolicyVersion;
                """, new { RoleId = roleId, PolicyVersion = policyVersion }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
            return rows.Length == 0 ? null : new RolePolicySnapshot(roleId, policyVersion,
                rows.Where(row => row.CapabilityId is not null && row.Scope is not null)
                    .Select(row => new CapabilityGrant(row.CapabilityId!, Enum.Parse<AuthorizationScope>(row.Scope!))).ToArray());
        }, ct);

    public ValueTask<bool> HasResourceGrantAsync(
        string tenantId, string actorId, string capabilityId, string resourceType, string resourceId, CancellationToken ct) =>
        QueryAsync(tenantId, connection => connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM am.resource_grants
                WHERE actor_id=@ActorId AND capability_id=@CapabilityId AND resource_type=@ResourceType AND resource_id=@ResourceId);
            """, new { ActorId = actorId, CapabilityId = capabilityId, ResourceType = resourceType, ResourceId = resourceId },
            cancellationToken: ct)), ct);

    public ValueTask<bool> IsDecisionStateCurrentAsync(
        string tenantId, string actorId, string roleId, long policyVersion, long subjectVersion, CancellationToken ct) =>
        QueryAsync(tenantId, connection => connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (SELECT 1 FROM am.access_subjects s CROSS JOIN am.policy_state p
                WHERE s.actor_id=@ActorId AND s.status='Active' AND s.business_role_id=@RoleId
                  AND s.subject_version=@SubjectVersion AND p.singleton AND p.policy_version=@PolicyVersion);
            """, new { ActorId = actorId, RoleId = roleId, PolicyVersion = policyVersion, SubjectVersion = subjectVersion },
            cancellationToken: ct)), ct);

    private async ValueTask<T> QueryAsync<T>(string tenantId, Func<NpgsqlConnection, Task<T>> query, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
            return await query(connection).ConfigureAwait(false);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private sealed record SubjectRow(
        string ActorId, string NormalizedEmail, string Status, string? BusinessRoleId,
        string? AdministrativeAuthority, long SubjectVersion, long PolicyVersion);
    private sealed record GrantRow(string RoleId, long PolicyVersion, string? CapabilityId, string? Scope);
}
