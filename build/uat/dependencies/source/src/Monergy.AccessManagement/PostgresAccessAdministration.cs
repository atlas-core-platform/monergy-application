using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed partial class PostgresAccessAdministration(
    ITenantConnectionFactory connections, CapabilityCatalog catalog, IVerifiedTenantIdentityDirectory identities,
    ITenantIdentityProvisioner? provisioner = null, AccessRequestTargetCatalog? requestTargetCatalog = null)
{
    public Task<AdministrationResult> SavePermissionAsync(
        TrustedActorContext context, string? permissionId, PermissionWrite request, CancellationToken ct)
    {
        var code = AccessInput.Code(request.Code);
        var label = AccessInput.Label(request.Label);
        AccessInput.Require(request.Grants is { Length: <= 1000 }, "INVALID_CAPABILITY_GRANTS");
        var grants = request.Grants.Distinct().ToArray();
        AccessInput.Require(grants.Length == request.Grants.Length, "DUPLICATE_CAPABILITY_GRANT");
        foreach (var grant in grants)
        {
            if (grant is null) throw new AccessAdministrationException("INVALID_CAPABILITY_GRANT", 400);
            AccessInput.Identifier(grant.CapabilityId, 160);
            AccessInput.Require(Enum.IsDefined(grant.Scope), "INVALID_CAPABILITY_GRANT");
            AccessInput.Require(catalog.TryGet(grant.CapabilityId, out var definition) &&
                definition is { Lifecycle: CapabilityLifecycle.Active }, "CAPABILITY_NOT_ASSIGNABLE");
            AccessInput.Require(grant.Scope != AuthorizationScope.Resource || definition!.ResourceType is not null,
                "RESOURCE_SCOPE_NOT_SUPPORTED");
        }
        var id = permissionId ?? Guid.NewGuid().ToString("N");
        AccessInput.Identifier(id);
        return MutateAsync(context, request.ExpectedPolicyVersion, id, "permission.saved", async (connection, transaction) =>
        {
            var sql = permissionId is null
                ? "INSERT INTO am.permissions (permission_id,code,label) VALUES (@Id,@Code,@Label);"
                : "UPDATE am.permissions SET code=@Code,label=@Label WHERE permission_id=@Id;";
            RequireFound(await connection.ExecuteAsync(Command(sql, new { Id = id, Code = code, Label = label }, transaction, ct)).ConfigureAwait(false));
            await connection.ExecuteAsync(Command("DELETE FROM am.permission_capabilities WHERE permission_id=@Id;",
                new { Id = id }, transaction, ct)).ConfigureAwait(false);
            foreach (var grant in grants)
                await connection.ExecuteAsync(Command("""
                    INSERT INTO am.permission_capabilities (permission_id,capability_id,scope) VALUES (@Id,@CapabilityId,@Scope);
                    """, new { Id = id, grant.CapabilityId, Scope = grant.Scope.ToString() }, transaction, ct)).ConfigureAwait(false);
            return new MutationEffect();
        }, ct);
    }

    public Task<AdministrationResult> SaveRoleAsync(
        TrustedActorContext context, string? roleId, RoleWrite request, CancellationToken ct)
    {
        var code = AccessInput.Code(request.Code);
        var label = AccessInput.Label(request.Label);
        AccessInput.Require(request.PermissionIds is { Length: <= 1000 }, "INVALID_PERMISSIONS");
        var permissions = request.PermissionIds.Distinct(StringComparer.Ordinal).ToArray();
        AccessInput.Require(permissions.Length == request.PermissionIds.Length, "DUPLICATE_PERMISSION");
        foreach (var permission in permissions) AccessInput.Identifier(permission);
        var id = roleId ?? Guid.NewGuid().ToString("N");
        AccessInput.Identifier(id);
        return MutateAsync(context, request.ExpectedPolicyVersion, id, "role.saved", async (connection, transaction) =>
        {
            var count = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT count(*) FROM am.permissions WHERE permission_id=ANY(@Ids);", new { Ids = permissions }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(count == permissions.Length, "PERMISSION_NOT_FOUND", 404);
            var sql = roleId is null
                ? "INSERT INTO am.roles (role_id,code,label) VALUES (@Id,@Code,@Label);"
                : "UPDATE am.roles SET code=@Code,label=@Label WHERE role_id=@Id;";
            RequireFound(await connection.ExecuteAsync(Command(sql, new { Id = id, Code = code, Label = label }, transaction, ct)).ConfigureAwait(false));
            await connection.ExecuteAsync(Command("DELETE FROM am.role_permissions WHERE role_id=@Id;",
                new { Id = id }, transaction, ct)).ConfigureAwait(false);
            foreach (var permission in permissions)
                await connection.ExecuteAsync(Command(
                    "INSERT INTO am.role_permissions (role_id,permission_id) VALUES (@Id,@PermissionId);",
                    new { Id = id, PermissionId = permission }, transaction, ct)).ConfigureAwait(false);
            return new MutationEffect();
        }, ct);
    }

    public async Task<DefaultAccessPackStatus> ReadDefaultAccessPackStatusAsync(
        TrustedActorContext context, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;",
                null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var anchor = await connection.QuerySingleAsync<PackStatusAnchorRow>(Command("""
                SELECT default_access_pack_version AS InstalledVersion,
                    default_access_pack_installed_at AS InstalledAt
                FROM am.tenant_identity WHERE singleton;
                """, null, transaction, ct)).ConfigureAwait(false);
            var latest = anchor.InstalledVersion is null
                ? null
                : await connection.QuerySingleOrDefaultAsync<PackStatusHistoryRow>(Command("""
                    SELECT pack_identity AS InstalledIdentity,operation_id AS LatestOperationId,
                        plan_hash AS LatestPlanHash,from_policy_version AS FromPolicyVersion,
                        to_policy_version AS ToPolicyVersion
                    FROM am.default_access_pack_history
                    WHERE pack_version=@InstalledVersion;
                    """, new { anchor.InstalledVersion }, transaction, ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(
                anchor.InstalledVersion,
                latest?.InstalledIdentity,
                anchor.InstalledAt is null
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(anchor.InstalledAt.Value, DateTimeKind.Utc)),
                policyVersion,
                latest?.LatestOperationId,
                latest?.LatestPlanHash,
                latest?.FromPolicyVersion,
                latest?.ToPolicyVersion);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public async Task<DefaultAccessPackPlan> PlanDefaultAccessPackAdoptionAsync(
        TrustedActorContext context, CancellationToken ct)
    {
        DefaultAccessPack.Validate(catalog);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var plan = await BuildDefaultAccessPackPlanAsync(connection, transaction, policyVersion, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return plan;
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public async Task<DefaultAccessPackAdoptionResult> AdoptDefaultAccessPackAsync(
        TrustedActorContext context, DefaultAccessPackAdoptionRequest request, CancellationToken ct)
    {
        AccessInput.Require(request.ExpectedPolicyVersion >= 1, "INVALID_POLICY_VERSION");
        AccessInput.Identifier(request.OperationId);
        AccessInput.Require(!string.IsNullOrWhiteSpace(request.PlanHash) && request.PlanHash.Length == 64 &&
            request.PlanHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "INVALID_PLAN_HASH");
        DefaultAccessPack.Validate(catalog);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var currentVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);

            var replay = await connection.QuerySingleOrDefaultAsync<PackHistoryRow>(Command("""
                SELECT pack_version AS PackVersion,plan_hash AS PlanHash,to_policy_version AS ToPolicyVersion
                FROM am.default_access_pack_history WHERE operation_id=@OperationId;
                """, new { request.OperationId }, transaction, ct)).ConfigureAwait(false);
            if (replay is not null)
            {
                AccessInput.Require(replay.PackVersion == DefaultAccessPack.Version &&
                    string.Equals(replay.PlanHash, request.PlanHash, StringComparison.Ordinal),
                    "DEFAULT_ACCESS_PACK_OPERATION_CONFLICT", 409);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new(DefaultAccessPack.Version, replay.ToPolicyVersion, request.OperationId, request.PlanHash, true);
            }

            AccessInput.Require(currentVersion == request.ExpectedPolicyVersion, "POLICY_VERSION_CONFLICT", 409);
            var plan = await BuildDefaultAccessPackPlanAsync(connection, transaction, currentVersion, ct).ConfigureAwait(false);
            AccessInput.Require(!plan.AlreadyInstalled, "DEFAULT_ACCESS_PACK_ALREADY_INSTALLED", 409);
            AccessInput.Require(plan.Conflicts.Count == 0, "DEFAULT_ACCESS_PACK_CUSTOMIZATION_REVIEW_REQUIRED", 409);
            AccessInput.Require(string.Equals(plan.PlanHash, request.PlanHash, StringComparison.Ordinal),
                "DEFAULT_ACCESS_PACK_PLAN_CHANGED", 409);

            foreach (var permission in DefaultAccessPack.Permissions)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS (SELECT 1 FROM am.permissions WHERE permission_id=@PermissionId);",
                    new { permission.PermissionId }, transaction, ct)).ConfigureAwait(false);
                if (exists)
                {
                    await connection.ExecuteAsync(Command(
                        "UPDATE am.permissions SET code=@Code,label=@Label WHERE permission_id=@PermissionId;",
                        permission, transaction, ct)).ConfigureAwait(false);
                    await connection.ExecuteAsync(Command(
                        "DELETE FROM am.permission_capabilities WHERE permission_id=@PermissionId;",
                        new { permission.PermissionId }, transaction, ct)).ConfigureAwait(false);
                }
                else
                {
                    await connection.ExecuteAsync(Command(
                        "INSERT INTO am.permissions(permission_id,code,label) VALUES (@PermissionId,@Code,@Label);",
                        permission, transaction, ct)).ConfigureAwait(false);
                }

                foreach (var grant in permission.Grants)
                    await connection.ExecuteAsync(Command("""
                        INSERT INTO am.permission_capabilities(permission_id,capability_id,scope)
                        VALUES (@PermissionId,@CapabilityId,@Scope);
                        """, new
                    {
                        permission.PermissionId,
                        grant.CapabilityId,
                        Scope = grant.Scope.ToString()
                    }, transaction, ct)).ConfigureAwait(false);
            }

            foreach (var role in DefaultAccessPack.Roles)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS (SELECT 1 FROM am.roles WHERE role_id=@RoleId);",
                    new { role.RoleId }, transaction, ct)).ConfigureAwait(false);
                if (exists)
                {
                    await connection.ExecuteAsync(Command(
                        "UPDATE am.roles SET code=@Code,label=@Label WHERE role_id=@RoleId;",
                        role, transaction, ct)).ConfigureAwait(false);
                    await connection.ExecuteAsync(Command(
                        "DELETE FROM am.role_permissions WHERE role_id=@RoleId;",
                        new { role.RoleId }, transaction, ct)).ConfigureAwait(false);
                }
                else
                {
                    await connection.ExecuteAsync(Command(
                        "INSERT INTO am.roles(role_id,code,label) VALUES (@RoleId,@Code,@Label);",
                        role, transaction, ct)).ConfigureAwait(false);
                }

                foreach (var permissionId in role.PermissionIds)
                    await connection.ExecuteAsync(Command(
                        "INSERT INTO am.role_permissions(role_id,permission_id) VALUES (@RoleId,@PermissionId);",
                        new { role.RoleId, PermissionId = permissionId }, transaction, ct)).ConfigureAwait(false);
            }

            var tenantUpdated = await connection.ExecuteAsync(Command("""
                UPDATE am.tenant_identity
                SET default_access_pack_version=@PackVersion,default_access_pack_installed_at=clock_timestamp()
                WHERE singleton AND default_access_pack_version=@ExpectedPackVersion;
                """, new
            {
                PackVersion = DefaultAccessPack.Version,
                ExpectedPackVersion = plan.InstalledVersion
            }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(tenantUpdated == 1, "DEFAULT_ACCESS_PACK_VERSION_CONFLICT", 409);

            var nextVersion = await connection.ExecuteScalarAsync<long>(Command("""
                UPDATE am.policy_state SET policy_version=policy_version+1
                WHERE singleton RETURNING policy_version;
                """, null, transaction, ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.default_access_pack_history (
                    pack_version,pack_identity,installed_at,operation_id,plan_hash,plan_document,
                    from_policy_version,to_policy_version,installed_by)
                VALUES (@PackVersion,@PackIdentity,clock_timestamp(),@OperationId,@PlanHash,@PlanDocument,
                    @FromPolicyVersion,@ToPolicyVersion,@InstalledBy);
                """, new
            {
                PackVersion = DefaultAccessPack.Version,
                PackIdentity = DefaultAccessPack.Identity,
                request.OperationId,
                request.PlanHash,
                PlanDocument = SerializePlanIntent(
                    plan.InstalledVersion, plan.TargetVersion, plan.PolicyVersion,
                    plan.AlreadyInstalled, plan.Changes, plan.Conflicts),
                FromPolicyVersion = currentVersion,
                ToPolicyVersion = nextVersion,
                InstalledBy = context.ActorId
            }, transaction, ct)).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(new
            {
                operation = "default-access-pack.adopted",
                operationId = request.OperationId,
                fromVersion = "1.0",
                toVersion = DefaultAccessPack.Version,
                planHash = request.PlanHash
            });
            foreach (var eventType in new[] { "AuthorizationPolicyChanged", "AccessManagementAuditEvidence" })
                await connection.ExecuteAsync(Command("""
                    INSERT INTO am.outbox (event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,
                        correlation_id,policy_version,subject_version,payload)
                    VALUES (@EventId,@EventType,@TenantId,@AggregateId,@ActorId,@CorrelationId,
                        @PolicyVersion,NULL,CAST(@Payload AS jsonb));
                    """, new
                {
                    EventId = Guid.NewGuid(),
                    EventType = eventType,
                    context.TenantId,
                    AggregateId = context.TenantId,
                    context.ActorId,
                    context.CorrelationId,
                    PolicyVersion = nextVersion,
                    Payload = payload
                }, transaction, ct)).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(DefaultAccessPack.Version, nextVersion, request.OperationId, request.PlanHash, false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation)
        {
            throw new AccessAdministrationException("POLICY_REFERENCE_CONFLICT", 409);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public Task<AdministrationResult> AddMemberAsync(TrustedActorContext context, MemberCreate request, CancellationToken ct)
    {
        AccessInput.Identifier(request.ActorId);
        return MutateAsync(context, request.ExpectedPolicyVersion, request.ActorId, "member.created", async (connection, transaction) =>
        {
            var identity = await identities.FindAsync(context.TenantId, request.ActorId, ct).ConfigureAwait(false);
            AccessInput.Require(identity is not null && identity.TenantId == context.TenantId && identity.ActorId == request.ActorId &&
                !string.IsNullOrWhiteSpace(identity.NormalizedEmail) &&
                identity.NormalizedEmail.Length <= 320, "VERIFIED_IDENTITY_REQUIRED", 409);
            var reserved = await connection.ExecuteScalarAsync<bool>(Command(
                "SELECT EXISTS (SELECT 1 FROM am.import_email_reservations WHERE normalized_email=@Email);",
                new { Email = identity!.NormalizedEmail.Trim().ToLowerInvariant() }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(!reserved, "EMAIL_RESERVED_BY_IMPORT", 409);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.access_subjects (actor_id,normalized_email,status)
                VALUES (@ActorId,@NormalizedEmail,'Active');
                """, new { ActorId = request.ActorId, NormalizedEmail = identity!.NormalizedEmail.Trim().ToLowerInvariant() },
                transaction, ct)).ConfigureAwait(false);
            return new MutationEffect(1, request.ActorId);
        }, ct);
    }

    public Task<AdministrationResult> UpdateMemberAsync(
        TrustedActorContext context, string actorId, MemberWrite request, CancellationToken ct)
    {
        AccessInput.Identifier(actorId);
        if (request.BusinessRoleId is not null) AccessInput.Identifier(request.BusinessRoleId);
        return MutateAsync(context, request.ExpectedPolicyVersion, actorId, "member.updated", async (connection, transaction) =>
        {
            var current = await connection.QuerySingleOrDefaultAsync<MemberRow>(Command("""
                SELECT status AS Status, business_role_id AS BusinessRoleId, administrative_authority AS Authority
                FROM am.access_subjects WHERE actor_id=@ActorId;
                """, new { ActorId = actorId }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(current is not null, "MEMBER_NOT_FOUND", 404);
            AccessInput.Require(current!.Status != "PendingIdentity", "IDENTITY_ACTIVATION_REQUIRED", 409);
            if (current.Status == "Active" && current.Authority == "TenantAdmin" && (!request.Active || !request.TenantAdmin))
            {
                var administrators = await connection.ExecuteScalarAsync<long>(Command("""
                    SELECT count(*) FROM am.access_subjects WHERE status='Active' AND administrative_authority='TenantAdmin';
                    """, null, transaction, ct)).ConfigureAwait(false);
                AccessInput.Require(administrators > 1, "LAST_ACTIVE_TENANT_ADMIN", 409);
            }
            if (request.BusinessRoleId is not null)
            {
                var exists = await connection.ExecuteScalarAsync<bool>(Command(
                    "SELECT EXISTS (SELECT 1 FROM am.roles WHERE role_id=@RoleId);",
                    new { RoleId = request.BusinessRoleId }, transaction, ct)).ConfigureAwait(false);
                AccessInput.Require(exists, "ROLE_NOT_FOUND", 404);
            }
            var version = await connection.ExecuteScalarAsync<long>(Command("""
                UPDATE am.access_subjects SET business_role_id=@RoleId, status=@Status,
                    administrative_authority=@Authority, subject_version=subject_version+1
                WHERE actor_id=@ActorId RETURNING subject_version;
                """, new
            {
                ActorId = actorId,
                RoleId = request.BusinessRoleId,
                Status = request.Active ? "Active" : "Disabled",
                Authority = request.TenantAdmin ? "TenantAdmin" : null
            },
                transaction, ct)).ConfigureAwait(false);
            if (current.BusinessRoleId != request.BusinessRoleId || !request.Active)
                await connection.ExecuteAsync(Command("""
                    DELETE FROM am.resource_grants WHERE actor_id=@ActorId;
                    DELETE FROM am.customer_relationships WHERE actor_id=@ActorId;
                    DELETE FROM am.subject_permissions WHERE actor_id=@ActorId;
                    """,
                    new { ActorId = actorId }, transaction, ct)).ConfigureAwait(false);
            return new MutationEffect(version, actorId);
        }, ct);
    }

    public Task<AdministrationResult> AddResourceGrantAsync(
        TrustedActorContext context, ResourceGrantWrite request, CancellationToken ct)
    {
        AccessInput.Identifier(request.ActorId);
        AccessInput.Identifier(request.ResourceId, 200);
        AccessInput.Identifier(request.CapabilityId, 160);
        AccessInput.Require(catalog.TryGet(request.CapabilityId, out var capability) &&
            capability is { Lifecycle: CapabilityLifecycle.Active, ResourceType: not null } &&
            capability.ResourceType == request.ResourceType, "INVALID_RESOURCE_CAPABILITY");
        var id = Guid.NewGuid().ToString("N");
        return MutateAsync(context, request.ExpectedPolicyVersion, id, "resource-grant.created", async (connection, transaction) =>
        {
            var eligible = await connection.ExecuteScalarAsync<bool>(Command("""
                SELECT EXISTS (
                    SELECT 1 FROM am.access_subjects s
                    JOIN am.role_permissions rp ON rp.role_id=s.business_role_id
                    JOIN am.permission_capabilities pc ON pc.permission_id=rp.permission_id
                    WHERE s.actor_id=@ActorId AND s.status='Active' AND pc.capability_id=@CapabilityId AND pc.scope='Resource');
                """, new { request.ActorId, request.CapabilityId }, transaction, ct)).ConfigureAwait(false);
            if (!eligible)
            {
                var subjectGrants = await connection.QueryAsync<ResourceSubjectGrantRow>(Command("""
                    SELECT sp.permission_id AS PermissionId,p.code AS PermissionCode,
                        pc.capability_id AS CapabilityId,pc.scope AS Scope,
                        apg.permission_code AS SnapshotPermissionCode,
                        apg.capability_id AS SnapshotCapabilityId,apg.scope AS SnapshotScope,
                        r.code AS RoleCode,ar.target_code AS TargetCode,
                        ar.target_definition_hash AS TargetDefinitionHash
                    FROM am.access_subjects s
                    JOIN am.roles r ON r.role_id=s.business_role_id
                    JOIN am.subject_permissions sp ON sp.actor_id=s.actor_id
                    JOIN am.permissions p ON p.permission_id=sp.permission_id
                    JOIN am.permission_capabilities pc ON pc.permission_id=sp.permission_id
                    JOIN am.access_request_permission_grants apg
                        ON apg.actor_id=sp.actor_id AND apg.permission_id=sp.permission_id
                    JOIN am.access_requests ar ON ar.request_id=apg.request_id
                    WHERE s.actor_id=@ActorId AND s.status='Active'
                      AND pc.capability_id=@CapabilityId AND pc.scope='Resource'
                      AND ar.state='Fulfilled' AND ar.requester_actor_id=s.actor_id
                      AND ar.requester_role_id=s.business_role_id;
                    """, new { request.ActorId, request.CapabilityId }, transaction, ct)).ConfigureAwait(false);
                eligible = subjectGrants.Any(row => RequestTargets.TryGet(row.TargetCode, out var target) &&
                    target is not null && target.DefinitionHash == row.TargetDefinitionHash &&
                    target.EligibleRoleCodes.Contains(row.RoleCode, StringComparer.Ordinal) &&
                    target.Permissions.Any(permission => permission.PermissionId == row.PermissionId &&
                        permission.PermissionCode == row.PermissionCode &&
                        permission.PermissionCode == row.SnapshotPermissionCode &&
                        permission.CapabilityId == row.CapabilityId &&
                        permission.CapabilityId == row.SnapshotCapabilityId &&
                        permission.Scope.ToString() == row.Scope &&
                        permission.Scope.ToString() == row.SnapshotScope));
            }
            AccessInput.Require(eligible, "RESOURCE_CAPABILITY_NOT_ASSIGNED", 409);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.resource_grants (grant_id,actor_id,capability_id,resource_type,resource_id)
                VALUES (@Id,@ActorId,@CapabilityId,@ResourceType,@ResourceId);
                """, new { Id = id, request.ActorId, request.CapabilityId, request.ResourceType, request.ResourceId },
                transaction, ct)).ConfigureAwait(false);
            var version = await IncrementSubjectAsync(connection, transaction, request.ActorId, ct).ConfigureAwait(false);
            return new MutationEffect(version, request.ActorId);
        }, ct);
    }

    public Task<AdministrationResult> DeleteAsync(
        TrustedActorContext context, AdministrationEntity entity, string id, long expectedVersion, CancellationToken ct)
    {
        AccessInput.Identifier(id);
        AccessInput.Require(entity is AdministrationEntity.Permissions or AdministrationEntity.Roles or AdministrationEntity.ResourceGrants or AdministrationEntity.Groups or AdministrationEntity.CustomerRelationships,
            "DELETE_NOT_SUPPORTED");
        return MutateAsync(context, expectedVersion, id, entity.ToString().ToLowerInvariant() + ".deleted", async (connection, transaction) =>
        {
            if (entity is AdministrationEntity.ResourceGrants or AdministrationEntity.CustomerRelationships)
            {
                var actor = await connection.QuerySingleOrDefaultAsync<string>(Command(
                    entity == AdministrationEntity.ResourceGrants
                        ? "DELETE FROM am.resource_grants WHERE grant_id=@Id RETURNING actor_id;"
                        : "DELETE FROM am.customer_relationships WHERE relationship_id=@Id RETURNING actor_id;",
                    new { Id = id }, transaction, ct)).ConfigureAwait(false);
                AccessInput.Require(actor is not null, "RESOURCE_GRANT_NOT_FOUND", 404);
                return new MutationEffect(await IncrementSubjectAsync(connection, transaction, actor!, ct).ConfigureAwait(false), actor);
            }
            var sql = entity switch
            {
                AdministrationEntity.Permissions => "DELETE FROM am.permissions WHERE permission_id=@Id;",
                AdministrationEntity.Roles => "DELETE FROM am.roles WHERE role_id=@Id;",
                _ => "DELETE FROM am.groups WHERE group_id=@Id;"
            };
            RequireFound(await connection.ExecuteAsync(Command(sql, new { Id = id }, transaction, ct)).ConfigureAwait(false));
            return new MutationEffect();
        }, ct);
    }

    public async Task<AdministrationPage> ListAsync(
        TrustedActorContext context, AdministrationEntity entity, string? after, int limit, CancellationToken ct)
    {
        AccessInput.Require(limit is >= 1 and <= 100, "INVALID_PAGE_SIZE");
        if (!string.IsNullOrEmpty(after)) AccessInput.Identifier(after);
        // Every fragment below is fixed service code. User input is parameterized.
        var source = entity switch
        {
            AdministrationEntity.Permissions => """
                SELECT p.permission_id AS id, jsonb_build_object('id',p.permission_id,'code',p.code,'label',p.label,
                    'grants',COALESCE((SELECT jsonb_agg(jsonb_build_object('capabilityId',pc.capability_id,'scope',pc.scope)
                        ORDER BY pc.capability_id,pc.scope) FROM am.permission_capabilities pc WHERE pc.permission_id=p.permission_id),'[]'::jsonb)) AS document
                FROM am.permissions p
                """,
            AdministrationEntity.Roles => """
                SELECT r.role_id AS id, jsonb_build_object('id',r.role_id,'code',r.code,'label',r.label,
                    'permissionIds',COALESCE((SELECT jsonb_agg(rp.permission_id ORDER BY rp.permission_id)
                        FROM am.role_permissions rp WHERE rp.role_id=r.role_id),'[]'::jsonb)) AS document FROM am.roles r
                """,
            AdministrationEntity.Members => """
                SELECT s.actor_id AS id, jsonb_build_object('actorId',s.actor_id,'normalizedEmail',s.normalized_email,
                    'status',s.status,'businessRoleId',s.business_role_id,'tenantAdmin',COALESCE(s.administrative_authority='TenantAdmin',false),
                    'subjectVersion',s.subject_version) AS document FROM am.access_subjects s
                """,
            AdministrationEntity.ResourceGrants => """
                SELECT g.grant_id AS id, jsonb_build_object('id',g.grant_id,'actorId',g.actor_id,'capabilityId',g.capability_id,
                    'resourceType',g.resource_type,'resourceId',g.resource_id) AS document FROM am.resource_grants g
                """,
            AdministrationEntity.CustomerRelationships => """
                SELECT relationship_id AS id, jsonb_build_object('id',relationship_id,'actorId',actor_id,
                    'customerId',customer_id,'assignedAt',assigned_at) AS document FROM am.customer_relationships
                """,
            AdministrationEntity.Groups => """
                SELECT g.group_id AS id, jsonb_build_object('id',g.group_id,'code',g.code,'label',g.label,
                    'memberCount',(SELECT count(*) FROM am.group_members m WHERE m.group_id=g.group_id)) AS document FROM am.groups g
                """,
            AdministrationEntity.Imports => """
                SELECT import_id AS id, jsonb_build_object('id',import_id,'status',status,'rowCount',row_count,
                    'contentHash',content_hash,'errorCode',error_code,'activatedPolicyVersion',activated_policy_version,
                    'attempts',attempts) AS document FROM am.import_batches
                """,
            _ => throw new AccessAdministrationException("INVALID_ENTITY", 400)
        };
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var version = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<DocumentRow>(Command(
                $"SELECT id AS Id, document::text AS Document FROM ({source}) entries WHERE id>@After ORDER BY id LIMIT @Limit;",
                new { After = after ?? "", Limit = limit + 1 }, transaction, ct)).ConfigureAwait(false)).ToArray();
            var items = rows.Take(limit).Select(row => JsonSerializer.Deserialize<JsonElement>(row.Document)).ToArray();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(version, items, rows.Length > limit ? rows[limit - 1].Id : null);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private static async Task<DefaultAccessPackPlan> BuildDefaultAccessPackPlanAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long policyVersion, CancellationToken ct)
    {
        var installedVersion = await connection.ExecuteScalarAsync<string?>(Command(
            "SELECT default_access_pack_version FROM am.tenant_identity WHERE singleton;",
            null, transaction, ct)).ConfigureAwait(false);
        var changes = new List<DefaultAccessPackChange>();
        var conflicts = new List<string>();

        if (installedVersion == DefaultAccessPack.Version)
            return CreatePackPlan(installedVersion, policyVersion, true, changes, conflicts);

        IReadOnlyList<DefaultPermissionTemplate> baselinePermissions;
        IReadOnlyList<DefaultRoleTemplate> baselineRoles;
        if (string.Equals(installedVersion, "1.0", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version1Permissions;
            baselineRoles = DefaultAccessPack.Version1Roles;
        }
        else if (string.Equals(installedVersion, "2.0", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version2Roles;
        }
        else if (string.Equals(installedVersion, "2.1", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version21Roles;
        }
        else if (string.Equals(installedVersion, "2.2", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version22Roles;
        }
        else if (string.Equals(installedVersion, "2.3", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version23Roles;
        }
        else if (string.Equals(installedVersion, "2.4", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version24Roles;
        }
        else if (string.Equals(installedVersion, "2.5", StringComparison.Ordinal))
        {
            baselinePermissions = DefaultAccessPack.Version2Permissions;
            baselineRoles = DefaultAccessPack.Version25Roles;
        }
        else
        {
            conflicts.Add(installedVersion is null
                ? "DEFAULT_ACCESS_PACK_NOT_INSTALLED"
                : "UNSUPPORTED_INSTALLED_PACK_VERSION:" + installedVersion);
            return CreatePackPlan(installedVersion, policyVersion, false, changes, conflicts);
        }

        var allPermissions = baselinePermissions.Concat(DefaultAccessPack.Permissions)
            .GroupBy(permission => permission.PermissionId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var permissionIds = allPermissions.Select(permission => permission.PermissionId).ToArray();
        var permissionCodes = allPermissions.Select(permission => permission.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var permissionRows = (await connection.QueryAsync<PermissionStateRow>(Command("""
            SELECT p.permission_id AS PermissionId,p.code AS Code,p.label AS Label,
                pc.capability_id AS CapabilityId,pc.scope AS Scope
            FROM am.permissions p
            LEFT JOIN am.permission_capabilities pc ON pc.permission_id=p.permission_id
            WHERE p.permission_id=ANY(@Ids) OR p.code=ANY(@Codes)
            ORDER BY p.permission_id,pc.capability_id,pc.scope;
            """, new { Ids = permissionIds, Codes = permissionCodes }, transaction, ct)).ConfigureAwait(false)).ToArray();

        foreach (var legacy in baselinePermissions)
        {
            var target = DefaultAccessPack.Permissions.SingleOrDefault(permission => permission.PermissionId == legacy.PermissionId);
            var current = ReadPermissionState(permissionRows, legacy.PermissionId);
            var codeOwner = permissionRows.FirstOrDefault(row =>
                string.Equals(row.Code, legacy.Code, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                conflicts.Add("SEEDED_PERMISSION_MISSING:" + legacy.PermissionId);
                continue;
            }
            if (codeOwner is not null && codeOwner.PermissionId != legacy.PermissionId)
            {
                conflicts.Add("PERMISSION_CODE_COLLISION:" + legacy.Code);
                continue;
            }
            if (!PermissionMatches(current, legacy))
            {
                conflicts.Add("SEEDED_PERMISSION_CUSTOMIZED:" + legacy.PermissionId);
                continue;
            }
            if (target is not null && !PermissionMatches(current, target))
                changes.Add(new("UPDATE_PERMISSION", legacy.PermissionId,
                    DescribePermission(legacy), DescribePermission(target)));
        }

        foreach (var target in DefaultAccessPack.Permissions.Where(permission =>
                     baselinePermissions.All(legacy => legacy.PermissionId != permission.PermissionId)))
        {
            var current = ReadPermissionState(permissionRows, target.PermissionId);
            var codeOwner = permissionRows.FirstOrDefault(row =>
                string.Equals(row.Code, target.Code, StringComparison.OrdinalIgnoreCase));
            if (current is null && codeOwner is null)
            {
                changes.Add(new("ADD_PERMISSION", target.PermissionId, null, DescribePermission(target)));
                continue;
            }
            conflicts.Add("PERMISSION_COLLISION:" + target.PermissionId);
        }

        var allRoles = baselineRoles.Concat(DefaultAccessPack.Roles)
            .GroupBy(role => role.RoleId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var roleIds = allRoles.Select(role => role.RoleId).ToArray();
        var roleCodes = allRoles.Select(role => role.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roleRows = (await connection.QueryAsync<RoleStateRow>(Command("""
            SELECT r.role_id AS RoleId,r.code AS Code,r.label AS Label,rp.permission_id AS PermissionId
            FROM am.roles r
            LEFT JOIN am.role_permissions rp ON rp.role_id=r.role_id
            WHERE r.role_id=ANY(@Ids) OR r.code=ANY(@Codes)
            ORDER BY r.role_id,rp.permission_id;
            """, new { Ids = roleIds, Codes = roleCodes }, transaction, ct)).ConfigureAwait(false)).ToArray();

        foreach (var legacy in baselineRoles)
        {
            var target = DefaultAccessPack.Roles.Single(role => role.RoleId == legacy.RoleId);
            var current = ReadRoleState(roleRows, legacy.RoleId);
            var codeOwner = roleRows.FirstOrDefault(row =>
                string.Equals(row.Code, legacy.Code, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                conflicts.Add("SEEDED_ROLE_MISSING:" + legacy.RoleId);
                continue;
            }
            if (codeOwner is not null && codeOwner.RoleId != legacy.RoleId)
            {
                conflicts.Add("ROLE_CODE_COLLISION:" + legacy.Code);
                continue;
            }
            if (!RoleMatches(current, legacy))
            {
                conflicts.Add("SEEDED_ROLE_CUSTOMIZED:" + legacy.RoleId);
                continue;
            }
            if (!RoleMatches(current, target))
                changes.Add(new("UPDATE_ROLE", legacy.RoleId, DescribeRole(legacy), DescribeRole(target)));
        }

        foreach (var target in DefaultAccessPack.Roles.Where(role =>
                     baselineRoles.All(legacy => legacy.RoleId != role.RoleId)))
        {
            var current = ReadRoleState(roleRows, target.RoleId);
            var codeOwner = roleRows.FirstOrDefault(row =>
                string.Equals(row.Code, target.Code, StringComparison.OrdinalIgnoreCase));
            if (current is null && codeOwner is null)
            {
                changes.Add(new("ADD_ROLE", target.RoleId, null, DescribeRole(target)));
                continue;
            }
            conflicts.Add("ROLE_COLLISION:" + target.RoleId);
        }

        if (conflicts.Count == 0)
            changes.Add(new("SET_PACK_VERSION", "default-access-pack", installedVersion, DefaultAccessPack.Version));
        return CreatePackPlan(installedVersion, policyVersion, false, changes, conflicts);
    }

    private static DefaultAccessPackPlan CreatePackPlan(
        string? installedVersion, long policyVersion, bool alreadyInstalled,
        IReadOnlyCollection<DefaultAccessPackChange> changes, IReadOnlyCollection<string> conflicts)
    {
        var orderedChanges = changes.OrderBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.ObjectId, StringComparer.Ordinal).ToArray();
        var orderedConflicts = conflicts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var canonical = SerializePlanIntent(
            installedVersion, DefaultAccessPack.Version, policyVersion,
            alreadyInstalled, orderedChanges, orderedConflicts);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(installedVersion, DefaultAccessPack.Version, policyVersion, hash,
            alreadyInstalled, orderedChanges, orderedConflicts);
    }

    private static string SerializePlanIntent(
        string? installedVersion,
        string targetVersion,
        long policyVersion,
        bool alreadyInstalled,
        IReadOnlyCollection<DefaultAccessPackChange> changes,
        IReadOnlyCollection<string> conflicts) =>
        JsonSerializer.Serialize(new
        {
            installedVersion,
            targetVersion,
            policyVersion,
            alreadyInstalled,
            changes,
            conflicts
        });

    private static PermissionState? ReadPermissionState(
        IReadOnlyCollection<PermissionStateRow> rows, string permissionId)
    {
        var matches = rows.Where(row => row.PermissionId == permissionId).ToArray();
        return matches.Length == 0 ? null : new(matches[0].PermissionId, matches[0].Code, matches[0].Label,
            matches.Where(row => row.CapabilityId is not null && row.Scope is not null)
                .Select(row => row.CapabilityId + ":" + row.Scope)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static bool PermissionMatches(PermissionState state, DefaultPermissionTemplate expected)
    {
        var expectedGrants = expected.Grants.Select(grant => grant.CapabilityId + ":" + grant.Scope)
            .OrderBy(value => value, StringComparer.Ordinal);
        return string.Equals(state.Code, expected.Code, StringComparison.Ordinal) &&
            string.Equals(state.Label, expected.Label, StringComparison.Ordinal) &&
            state.Grants.SequenceEqual(expectedGrants, StringComparer.Ordinal);
    }

    private static string DescribePermission(DefaultPermissionTemplate permission) =>
        permission.Code + "|" + permission.Label + "|" +
        string.Join(",", permission.Grants.Select(grant => grant.CapabilityId + ":" + grant.Scope)
            .OrderBy(value => value, StringComparer.Ordinal));

    private static RoleState? ReadRoleState(IReadOnlyCollection<RoleStateRow> rows, string roleId)
    {
        var matches = rows.Where(row => row.RoleId == roleId).ToArray();
        return matches.Length == 0 ? null : new(matches[0].RoleId, matches[0].Code, matches[0].Label,
            matches.Where(row => row.PermissionId is not null).Select(row => row.PermissionId!)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static bool RoleMatches(RoleState state, DefaultRoleTemplate expected) =>
        string.Equals(state.Code, expected.Code, StringComparison.Ordinal) &&
        string.Equals(state.Label, expected.Label, StringComparison.Ordinal) &&
        state.PermissionIds.SequenceEqual(expected.PermissionIds.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string DescribeRole(DefaultRoleTemplate role) =>
        role.Code + "|" + role.Label + "|" +
        string.Join(",", role.PermissionIds.OrderBy(value => value, StringComparer.Ordinal));

    private async Task<AdministrationResult> MutateAsync(
        TrustedActorContext context, long expectedVersion, string id, string operation,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<MutationEffect>> mutation, CancellationToken ct)
    {
        AccessInput.Require(expectedVersion >= 1, "INVALID_POLICY_VERSION");
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var currentVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            AccessInput.Require(currentVersion == expectedVersion, "POLICY_VERSION_CONFLICT", 409);
            var effect = await mutation(connection, transaction).ConfigureAwait(false);
            var nextVersion = await connection.ExecuteScalarAsync<long>(Command("""
                UPDATE am.policy_state SET policy_version=policy_version+1 WHERE singleton RETURNING policy_version;
                """, null, transaction, ct)).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new { operation, entityId = id, subjectActorId = effect.ActorId });
            foreach (var eventType in effect.SubjectVersion is null
                ? new[] { "AuthorizationPolicyChanged", "AccessManagementAuditEvidence" }
                : new[] { "AuthorizationPolicyChanged", "TenantUserAccessChanged", "AccessManagementAuditEvidence" })
            {
                await connection.ExecuteAsync(Command("""
                    INSERT INTO am.outbox (event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,correlation_id,
                        policy_version,subject_version,payload)
                    VALUES (@EventId,@EventType,@TenantId,@AggregateId,@ActorId,@CorrelationId,@PolicyVersion,@SubjectVersion,CAST(@Payload AS jsonb));
                    """, new
                {
                    EventId = Guid.NewGuid(),
                    EventType = eventType,
                    context.TenantId,
                    AggregateId = eventType == "AuthorizationPolicyChanged" ? context.TenantId :
                            eventType == "TenantUserAccessChanged" ? effect.ActorId! : id,
                    context.ActorId,
                    context.CorrelationId,
                    PolicyVersion = nextVersion,
                    effect.SubjectVersion,
                    Payload = payload
                },
                    transaction, ct)).ConfigureAwait(false);
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(id, nextVersion, effect.SubjectVersion);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation)
        {
            throw new AccessAdministrationException("POLICY_REFERENCE_CONFLICT", 409);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw new AccessAdministrationException("INVALID_POLICY_DATA", 400);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private static async Task RequireAdministratorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, CancellationToken ct)
    {
        var allowed = await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT EXISTS (SELECT 1 FROM am.access_subjects WHERE actor_id=@ActorId
                AND status='Active' AND administrative_authority='TenantAdmin');
            """, new { ActorId = actorId }, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(allowed, "TENANT_ADMIN_REQUIRED", 403);
    }

    private static Task<long> IncrementSubjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<long>(Command("""
            UPDATE am.access_subjects SET subject_version=subject_version+1 WHERE actor_id=@ActorId RETURNING subject_version;
            """, new { ActorId = actorId }, transaction, ct));

    private static CommandDefinition Command(string sql, object? parameters, NpgsqlTransaction transaction, CancellationToken ct) =>
        new(sql, parameters, transaction, cancellationToken: ct);
    private static void RequireFound(int affected) => AccessInput.Require(affected == 1, "POLICY_OBJECT_NOT_FOUND", 404);
    private sealed record PackHistoryRow(string PackVersion, string? PlanHash, long ToPolicyVersion);
    private sealed record PackStatusAnchorRow(string? InstalledVersion, DateTime? InstalledAt);
    private sealed record PackStatusHistoryRow(
        string InstalledIdentity, string LatestOperationId, string? LatestPlanHash,
        long FromPolicyVersion, long ToPolicyVersion);
    private sealed record PermissionStateRow(
        string PermissionId, string Code, string Label, string? CapabilityId, string? Scope);
    private sealed record PermissionState(
        string PermissionId, string Code, string Label, IReadOnlyList<string> Grants);
    private sealed record RoleStateRow(string RoleId, string Code, string Label, string? PermissionId);
    private sealed record RoleState(string RoleId, string Code, string Label, IReadOnlyList<string> PermissionIds);
    private sealed record MutationEffect(long? SubjectVersion = null, string? ActorId = null);
    private sealed record MemberRow(string Status, string? BusinessRoleId, string? Authority);
    private sealed record ResourceSubjectGrantRow(
        string PermissionId, string PermissionCode, string CapabilityId, string Scope,
        string SnapshotPermissionCode, string SnapshotCapabilityId, string SnapshotScope,
        string RoleCode, string TargetCode, string TargetDefinitionHash);
    private sealed record DocumentRow(string Id, string Document);
}
