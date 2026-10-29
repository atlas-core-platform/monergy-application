using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed record AccessRequestSubmission(string OperationId);
public sealed record AccessRequestDecision(string OperationId);
public sealed record AccessRequestTargetStatus(
    string? RequestId,
    string Target,
    string TargetLabel,
    string GrantExplanation,
    string State,
    bool HasAccess,
    bool CanRequest,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DecisionAt,
    DateTimeOffset? FulfilledAt,
    string? DecisionReason);
public sealed record AccessRequestStatusPage(
    string TenantId, string ActorId, long PolicyVersion, long SubjectVersion,
    IReadOnlyList<AccessRequestTargetStatus> Items);
public sealed record AccessRequestReviewItem(
    string RequestId,
    string Requester,
    string RequesterRole,
    string Target,
    string TargetLabel,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecisionAt,
    string? DecisionReason,
    string GrantExplanation);
public sealed record AccessRequestReviewPage(
    string TenantId, string ActorId, long PolicyVersion,
    IReadOnlyList<AccessRequestReviewItem> Items, string? NextCursor);

public sealed partial class PostgresAccessAdministration
{
    private AccessRequestTargetCatalog RequestTargets => requestTargetCatalog ?? new(catalog);

    public async Task<AccessRequestStatusPage> ReadAccessRequestsAsync(
        TrustedActorContext context, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            var subject = await RequireBusinessSubjectAsync(connection, transaction, context.ActorId, false, ct).ConfigureAwait(false);
            var items = new List<AccessRequestTargetStatus>();
            foreach (var target in RequestTargets.All.Where(value =>
                         value.EligibleRoleCodes.Contains(subject.RoleCode, StringComparer.Ordinal)).OrderBy(value => value.Code))
            {
                var available = await TargetAvailableAsync(connection, transaction, target, ct).ConfigureAwait(false);
                var hasAccess = available && await HasTargetAsync(connection, transaction, context.ActorId, target, ct).ConfigureAwait(false);
                var row = await LatestAsync(connection, transaction, context.TenantId, context.ActorId, target.Code, ct)
                    .ConfigureAwait(false);
                items.Add(Status(target, row, hasAccess, available && !hasAccess && row?.State != "Submitted"));
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(context.TenantId, context.ActorId, policyVersion, subject.SubjectVersion, items);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    // Compatibility wrapper only. The definition and grant path are the generic catalog path.
    public async Task<AccessRequestTargetStatus> ReadAdvisorAccessRequestAsync(
        TrustedActorContext context, CancellationToken ct)
    {
        var page = await ReadAccessRequestsAsync(context, ct).ConfigureAwait(false);
        return page.Items.Single(value => value.Target == AccessRequestTargetCatalog.AdvisorTarget);
    }

    public Task<AccessRequestTargetStatus> SubmitAdvisorAccessRequestAsync(
        TrustedActorContext context, AccessRequestSubmission request, CancellationToken ct) =>
        SubmitAccessRequestAsync(context, AccessRequestTargetCatalog.AdvisorTarget, request, ct);

    public async Task<AccessRequestTargetStatus> SubmitAccessRequestAsync(
        TrustedActorContext context, string targetCode, AccessRequestSubmission request, CancellationToken ct)
    {
        OperationId(request.OperationId);
        AccessInput.Require(RequestTargets.TryGet(targetCode, out var target) && target is not null && target.TenantGrantable,
            "ACCESS_REQUEST_TARGET_NOT_FOUND", 404);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtext(@LockKey));",
                new { LockKey = context.ActorId + ":" + targetCode }, transaction, ct)).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            var subject = await RequireBusinessSubjectAsync(connection, transaction, context.ActorId, true, ct).ConfigureAwait(false);
            AccessInput.Require(target!.EligibleRoleCodes.Contains(subject.RoleCode, StringComparer.Ordinal),
                "ACCESS_REQUEST_TARGET_NOT_ELIGIBLE", 403);

            // Network retry replay is evaluated before current-access checks, including after fulfillment.
            var replay = await connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
                SELECT request_id AS RequestId,target_code AS TargetCode,target_definition_hash AS TargetDefinitionHash,
                    state AS State,created_at AS CreatedAt,decision_at AS DecisionAt,
                    fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
                FROM am.access_requests
                WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND submit_operation_id=@OperationId;
                """, new { context.TenantId, context.ActorId, request.OperationId }, transaction, ct)).ConfigureAwait(false);
            if (replay is not null)
            {
                AccessInput.Require(replay.TargetCode == target.Code, "ACCESS_REQUEST_OPERATION_CONFLICT", 409);
                var replayHasAccess = await HasTargetAsync(connection, transaction, context.ActorId, target, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Status(target, replay, replayHasAccess, false);
            }

            AccessInput.Require(await TargetAvailableAsync(connection, transaction, target, ct).ConfigureAwait(false),
                "ACCESS_REQUEST_TARGET_UNAVAILABLE", 409);
            AccessInput.Require(!await HasTargetAsync(connection, transaction, context.ActorId, target, ct).ConfigureAwait(false),
                "ACCESS_ALREADY_AVAILABLE", 409);

            var existing = await LatestSubmittedAsync(connection, transaction, context.TenantId, context.ActorId, target.Code, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Status(target, existing, false, false);
            }

            var id = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.access_requests (
                    request_id,tenant_id,requester_actor_id,target_code,target_definition_hash,
                    requester_role_id,requester_subject_version,submit_operation_id,state,expires_at)
                VALUES (@RequestId,@TenantId,@ActorId,@Target,@DefinitionHash,@RoleId,@SubjectVersion,
                    @OperationId,'Submitted',clock_timestamp()+interval '7 days');
                INSERT INTO am.access_request_events (
                    event_id,request_id,event_type,actor_id,policy_version)
                VALUES (@EventId,@RequestId,'Submitted',@ActorId,@PolicyVersion);
                """, new
            {
                RequestId = id,
                context.TenantId,
                ActorId = context.ActorId,
                Target = target.Code,
                DefinitionHash = target.DefinitionHash,
                subject.RoleId,
                subject.SubjectVersion,
                request.OperationId,
                EventId = Guid.NewGuid(),
                PolicyVersion = policyVersion
            }, transaction, ct)).ConfigureAwait(false);
            await WriteAccessRequestAuditAsync(connection, transaction, context, id, target.Code, policyVersion,
                subject.SubjectVersion, "access-request.submitted", context.ActorId, ct).ConfigureAwait(false);
            var created = await LatestAsync(connection, transaction, context.TenantId, context.ActorId, target.Code, ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Status(target, created!, false, false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new AccessAdministrationException("ACCESS_REQUEST_CONFLICT", 409);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public async Task<AccessRequestReviewPage> ListAccessRequestsAsync(
        TrustedActorContext context, string? after, int limit, CancellationToken ct)
    {
        if (after is not null) RequestId(after);
        AccessInput.Require(limit is >= 1 and <= 100, "INVALID_PAGE_SIZE");
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<AccessRequestReviewRow>(Command("""
                WITH anchor AS (
                    SELECT created_at,request_id FROM am.access_requests
                    WHERE tenant_id=@TenantId AND request_id=@After
                )
                SELECT r.request_id AS RequestId,s.normalized_email AS Requester,
                    COALESCE(rr.label,'Former business role') AS RequesterRole,r.target_code AS TargetCode,
                    r.target_definition_hash AS TargetDefinitionHash,r.state AS State,r.created_at AS CreatedAt,
                    r.expires_at AS ExpiresAt,r.decision_at AS DecisionAt,r.decision_reason AS DecisionReason
                FROM am.access_requests r
                JOIN am.access_subjects s ON s.actor_id=r.requester_actor_id
                LEFT JOIN am.roles rr ON rr.role_id=r.requester_role_id
                WHERE r.tenant_id=@TenantId AND (@After IS NULL OR
                    (r.created_at,r.request_id) < ((SELECT created_at FROM anchor),(SELECT request_id FROM anchor)))
                ORDER BY r.created_at DESC,r.request_id DESC LIMIT @Take;
                """, new { context.TenantId, After = after, Take = limit + 1 }, transaction, ct)).ConfigureAwait(false)).ToArray();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            var items = rows.Take(limit).Select(Review).ToArray();
            return new(context.TenantId, context.ActorId, policyVersion, items,
                rows.Length > limit ? items[^1].RequestId : null);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public Task<AccessRequestReviewItem> ApproveAccessRequestAsync(
        TrustedActorContext context, string requestId, AccessRequestDecision decision, CancellationToken ct) =>
        DecideAccessRequestAsync(context, requestId, decision, true, ct);

    public Task<AccessRequestReviewItem> RejectAccessRequestAsync(
        TrustedActorContext context, string requestId, AccessRequestDecision decision, CancellationToken ct) =>
        DecideAccessRequestAsync(context, requestId, decision, false, ct);

    private async Task<AccessRequestReviewItem> DecideAccessRequestAsync(
        TrustedActorContext context, string requestId, AccessRequestDecision decision, bool approve, CancellationToken ct)
    {
        RequestId(requestId);
        OperationId(decision.OperationId);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var request = await DecisionAsync(connection, transaction, context.TenantId, requestId, true, ct).ConfigureAwait(false);
            AccessInput.Require(request is not null, "ACCESS_REQUEST_NOT_FOUND", 404);
            AccessInput.Require(request!.RequesterActorId != context.ActorId, "ACCESS_REQUEST_SELF_REVIEW_DENIED", 403);
            if (request.State != "Submitted")
            {
                AccessInput.Require(request.DecisionAction == (approve ? "Approve" : "Reject"),
                    "ACCESS_REQUEST_ALREADY_DECIDED", 409);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Review(request);
            }

            RequestTargets.TryGet(request.TargetCode, out var target);
            var reason = approve
                ? await ApprovalFailureAsync(connection, transaction, request, target, ct).ConfigureAwait(false)
                : "AdministratorRejected";
            if (reason is not null)
            {
                await RejectAsync(connection, transaction, context, request, decision.OperationId,
                    approve ? "Approve" : "Reject", reason, policyVersion, ct).ConfigureAwait(false);
            }
            else
            {
                var approvedTarget = target!;
                var permissionIds = approvedTarget.Permissions.Select(value => value.PermissionId).ToArray();
                var inserted = await connection.ExecuteScalarAsync<int>(Command("""
                    WITH inserted AS (
                        INSERT INTO am.subject_permissions (
                            actor_id,permission_id,granted_at,granted_by,source_request_id)
                        SELECT @ActorId,required.permission_id,clock_timestamp(),@GrantedBy,@RequestId
                        FROM unnest(@PermissionIds) AS required(permission_id)
                        WHERE NOT EXISTS (
                            SELECT 1 FROM am.role_permissions rp
                            WHERE rp.role_id=@RoleId AND rp.permission_id=required.permission_id)
                        ON CONFLICT (actor_id,permission_id) DO NOTHING
                        RETURNING permission_id
                    ) SELECT count(*)::integer FROM inserted;
                    """, new
                {
                    ActorId = request.RequesterActorId,
                    PermissionIds = permissionIds,
                    GrantedBy = context.ActorId,
                    RequestId = request.RequestId,
                    RoleId = request.RequesterRoleId
                }, transaction, ct)).ConfigureAwait(false);
                await connection.ExecuteAsync(Command("""
                    INSERT INTO am.access_request_permission_grants (
                        request_id,actor_id,permission_id,permission_code,capability_id,scope)
                    SELECT @RequestId,@ActorId,snapshot.permission_id,snapshot.permission_code,
                        snapshot.capability_id,snapshot.scope
                    FROM unnest(@PermissionIds,@PermissionCodes,@CapabilityIds,@Scopes)
                        AS snapshot(permission_id,permission_code,capability_id,scope)
                    ON CONFLICT DO NOTHING;
                    """, new
                {
                    RequestId = request.RequestId,
                    ActorId = request.RequesterActorId,
                    PermissionIds = permissionIds,
                    PermissionCodes = approvedTarget.Permissions.Select(value => value.PermissionCode).ToArray(),
                    CapabilityIds = approvedTarget.Permissions.Select(value => value.CapabilityId).ToArray(),
                    Scopes = approvedTarget.Permissions.Select(value => value.Scope.ToString()).ToArray()
                }, transaction, ct)).ConfigureAwait(false);

                var nextPolicyVersion = policyVersion;
                long? subjectVersion = null;
                if (inserted > 0)
                {
                    subjectVersion = await IncrementSubjectAsync(connection, transaction,
                        request.RequesterActorId, ct).ConfigureAwait(false);
                    nextPolicyVersion = await connection.ExecuteScalarAsync<long>(Command("""
                        UPDATE am.policy_state SET policy_version=policy_version+1
                        WHERE singleton RETURNING policy_version;
                        """, null, transaction, ct)).ConfigureAwait(false);
                }
                await connection.ExecuteAsync(Command("""
                    UPDATE am.access_requests SET state='Fulfilled',decision_at=clock_timestamp(),
                        deciding_actor_id=@DecidingActorId,decision_action='Approve',
                        decision_operation_id=@OperationId,decision_reason=NULL,fulfilled_at=clock_timestamp()
                    WHERE request_id=@RequestId;
                    INSERT INTO am.access_request_events (
                        event_id,request_id,event_type,actor_id,policy_version)
                    VALUES (@ApprovedEvent,@RequestId,'Approved',@DecidingActorId,@PolicyVersion),
                           (@FulfilledEvent,@RequestId,'Fulfilled',@DecidingActorId,@PolicyVersion);
                    """, new
                {
                    DecidingActorId = context.ActorId,
                    decision.OperationId,
                    request.RequestId,
                    ApprovedEvent = Guid.NewGuid(),
                    FulfilledEvent = Guid.NewGuid(),
                    PolicyVersion = nextPolicyVersion
                }, transaction, ct)).ConfigureAwait(false);
                await WriteAccessRequestAuditAsync(connection, transaction, context, request.RequestId,
                    request.TargetCode, nextPolicyVersion, subjectVersion, "access-request.fulfilled",
                    request.RequesterActorId, ct, inserted > 0).ConfigureAwait(false);
            }

            var decided = await DecisionAsync(connection, transaction, context.TenantId, request.RequestId, false, ct)
                .ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Review(decided!);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new AccessAdministrationException("ACCESS_REQUEST_OPERATION_CONFLICT", 409);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private async Task<string?> ApprovalFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DecisionRow request,
        AccessRequestTargetDefinition? target, CancellationToken ct)
    {
        if (target is null || target.DefinitionHash != request.TargetDefinitionHash || !target.TenantGrantable ||
            !await TargetAvailableAsync(connection, transaction, target, ct).ConfigureAwait(false))
            return "CapabilityUnavailable";
        var expired = await connection.ExecuteScalarAsync<bool>(Command(
            "SELECT clock_timestamp()>=@ExpiresAt;", new { request.ExpiresAt }, transaction, ct)).ConfigureAwait(false);
        if (expired) return "RequestExpired";
        var current = await connection.QuerySingleOrDefaultAsync<BusinessSubjectRow>(Command("""
            SELECT s.business_role_id AS RoleId,s.subject_version AS SubjectVersion,
                s.status AS Status,r.code AS RoleCode
            FROM am.access_subjects s LEFT JOIN am.roles r ON r.role_id=s.business_role_id
            WHERE s.actor_id=@ActorId FOR SHARE OF s;
            """, new { ActorId = request.RequesterActorId }, transaction, ct)).ConfigureAwait(false);
        return current is { Status: "Active", RoleCode: not null } &&
            target.EligibleRoleCodes.Contains(current.RoleCode, StringComparer.Ordinal) &&
            current.RoleId == request.RequesterRoleId && current.SubjectVersion == request.RequesterSubjectVersion
                ? null
                : "RequesterNoLongerEligible";
    }

    private static async Task RejectAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TrustedActorContext context,
        DecisionRow request, string operationId, string action, string reason, long policyVersion, CancellationToken ct)
    {
        await connection.ExecuteAsync(Command("""
            UPDATE am.access_requests SET state='Rejected',decision_at=clock_timestamp(),
                deciding_actor_id=@DecidingActorId,decision_action=@Action,
                decision_operation_id=@OperationId,decision_reason=@Reason
            WHERE request_id=@RequestId;
            INSERT INTO am.access_request_events (
                event_id,request_id,event_type,actor_id,policy_version,reason)
            VALUES (@EventId,@RequestId,'Rejected',@DecidingActorId,@PolicyVersion,@Reason);
            """, new
        {
            DecidingActorId = context.ActorId,
            Action = action,
            OperationId = operationId,
            Reason = reason,
            request.RequestId,
            EventId = Guid.NewGuid(),
            PolicyVersion = policyVersion
        }, transaction, ct)).ConfigureAwait(false);
        await WriteAccessRequestAuditAsync(connection, transaction, context, request.RequestId,
            request.TargetCode, policyVersion, null, "access-request.rejected", request.RequesterActorId, ct)
            .ConfigureAwait(false);
    }

    private static async Task<BusinessSubjectRow> RequireBusinessSubjectAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, bool forShare, CancellationToken ct)
    {
        var subject = await connection.QuerySingleOrDefaultAsync<BusinessSubjectRow>(Command($"""
            SELECT s.business_role_id AS RoleId,s.subject_version AS SubjectVersion,
                s.status AS Status,r.code AS RoleCode
            FROM am.access_subjects s JOIN am.roles r ON r.role_id=s.business_role_id
            WHERE s.actor_id=@ActorId {(forShare ? "FOR SHARE OF s,r" : string.Empty)};
            """, new { ActorId = actorId }, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(subject is { Status: "Active", RoleId: not null, RoleCode: not null },
            "BUSINESS_ROLE_REQUIRED", 403);
        return subject!;
    }

    private async Task<bool> TargetAvailableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        AccessRequestTargetDefinition target, CancellationToken ct)
    {
        foreach (var permission in target.Permissions)
            if (!catalog.TryGet(permission.CapabilityId, out var capability) ||
                capability is not { Lifecycle: CapabilityLifecycle.Active }) return false;
        var rows = (await connection.QueryAsync<PermissionDefinitionRow>(Command("""
            SELECT p.permission_id AS PermissionId,p.code AS PermissionCode,
                pc.capability_id AS CapabilityId,pc.scope AS Scope
            FROM am.permissions p JOIN am.permission_capabilities pc ON pc.permission_id=p.permission_id
            WHERE p.permission_id=ANY(@PermissionIds);
            """, new { PermissionIds = target.Permissions.Select(value => value.PermissionId).ToArray() }, transaction, ct))
            .ConfigureAwait(false)).ToArray();
        return rows.Length == target.Permissions.Count && target.Permissions.All(expected => rows.Count(row =>
            row.PermissionId == expected.PermissionId && row.PermissionCode == expected.PermissionCode &&
            row.CapabilityId == expected.CapabilityId && row.Scope == expected.Scope.ToString()) == 1);
    }

    private async Task<bool> HasTargetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId,
        AccessRequestTargetDefinition target, CancellationToken ct)
    {
        var capabilities = target.Permissions.Select(value => value.CapabilityId).Distinct().ToArray();
        var effective = (await connection.QueryAsync<string>(Command("""
            SELECT DISTINCT pc.capability_id
            FROM am.access_subjects s
            JOIN am.role_permissions rp ON rp.role_id=s.business_role_id
            JOIN am.permission_capabilities pc ON pc.permission_id=rp.permission_id
            WHERE s.actor_id=@ActorId AND pc.capability_id=ANY(@Capabilities);
            """, new { ActorId = actorId, Capabilities = capabilities }, transaction, ct)).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var rows = await connection.QueryAsync<SubjectGrantRow>(Command("""
            SELECT sp.permission_id AS PermissionId,p.code AS PermissionCode,
                pc.capability_id AS CapabilityId,pc.scope AS Scope,
                apg.permission_code AS SnapshotPermissionCode,
                apg.capability_id AS SnapshotCapabilityId,apg.scope AS SnapshotScope,
                ar.target_code AS TargetCode,ar.target_definition_hash AS TargetDefinitionHash,
                r.code AS RoleCode
            FROM am.subject_permissions sp
            JOIN am.access_request_permission_grants apg
                ON apg.actor_id=sp.actor_id AND apg.permission_id=sp.permission_id
            JOIN am.access_requests ar ON ar.request_id=apg.request_id
            JOIN am.permissions p ON p.permission_id=sp.permission_id
            JOIN am.permission_capabilities pc ON pc.permission_id=sp.permission_id
            JOIN am.access_subjects s ON s.actor_id=sp.actor_id
            JOIN am.roles r ON r.role_id=s.business_role_id
            WHERE sp.actor_id=@ActorId AND ar.state='Fulfilled' AND pc.capability_id=ANY(@Capabilities);
            """, new { ActorId = actorId, Capabilities = capabilities }, transaction, ct)).ConfigureAwait(false);
        foreach (var row in rows)
            if (RequestTargets.TryGet(row.TargetCode, out var source) && source is not null &&
                source.DefinitionHash == row.TargetDefinitionHash &&
                source.EligibleRoleCodes.Contains(row.RoleCode, StringComparer.Ordinal) &&
                source.Permissions.Any(permission => permission.PermissionId == row.PermissionId &&
                    permission.PermissionCode == row.PermissionCode &&
                    permission.PermissionCode == row.SnapshotPermissionCode &&
                    permission.CapabilityId == row.CapabilityId &&
                    permission.CapabilityId == row.SnapshotCapabilityId &&
                    permission.Scope.ToString() == row.Scope &&
                    permission.Scope.ToString() == row.SnapshotScope))
                effective.Add(row.CapabilityId);
        return capabilities.All(effective.Contains);
    }

    private static Task<AccessRequestRow?> LatestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string actorId, string target, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
            SELECT request_id AS RequestId,target_code AS TargetCode,target_definition_hash AS TargetDefinitionHash,
                state AS State,created_at AS CreatedAt,decision_at AS DecisionAt,
                fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
            FROM am.access_requests
            WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND target_code=@Target
            ORDER BY created_at DESC,request_id DESC LIMIT 1;
            """, new { TenantId = tenantId, ActorId = actorId, Target = target }, transaction, ct));

    private static Task<AccessRequestRow?> LatestSubmittedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string actorId, string target, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
            SELECT request_id AS RequestId,target_code AS TargetCode,target_definition_hash AS TargetDefinitionHash,
                state AS State,created_at AS CreatedAt,decision_at AS DecisionAt,
                fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
            FROM am.access_requests
            WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND target_code=@Target AND state='Submitted';
            """, new { TenantId = tenantId, ActorId = actorId, Target = target }, transaction, ct));

    private static Task<DecisionRow?> DecisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId,
        string requestId, bool forUpdate, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<DecisionRow>(Command($"""
            SELECT r.request_id AS RequestId,r.requester_actor_id AS RequesterActorId,
                s.normalized_email AS Requester,
                COALESCE(rr.label,'Former business role') AS RequesterRole,r.target_code AS TargetCode,
                r.target_definition_hash AS TargetDefinitionHash,r.requester_role_id AS RequesterRoleId,
                r.requester_subject_version AS RequesterSubjectVersion,r.state AS State,
                r.created_at AS CreatedAt,r.expires_at AS ExpiresAt,r.decision_at AS DecisionAt,
                r.decision_action AS DecisionAction,r.decision_operation_id AS DecisionOperationId,
                r.decision_reason AS DecisionReason
            FROM am.access_requests r JOIN am.access_subjects s ON s.actor_id=r.requester_actor_id
            LEFT JOIN am.roles rr ON rr.role_id=r.requester_role_id
            WHERE r.tenant_id=@TenantId AND r.request_id=@RequestId {(forUpdate ? "FOR UPDATE OF r" : string.Empty)};
            """, new { TenantId = tenantId, RequestId = requestId }, transaction, ct));

    private static async Task WriteAccessRequestAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TrustedActorContext context,
        string requestId, string target, long policyVersion, long? subjectVersion, string operation,
        string subjectActorId, CancellationToken ct, bool authorizationChanged = false)
    {
        var payload = JsonSerializer.Serialize(new { operation, requestId, target, subjectActorId });
        var eventTypes = authorizationChanged
            ? new[] { "AuthorizationPolicyChanged", "TenantUserAccessChanged", "AccessManagementAuditEvidence" }
            : new[] { "AccessManagementAuditEvidence" };
        foreach (var eventType in eventTypes)
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.outbox (event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,
                    correlation_id,policy_version,subject_version,payload)
                VALUES (@EventId,@EventType,@TenantId,@AggregateId,@InitiatorActorId,
                    @CorrelationId,@PolicyVersion,@SubjectVersion,CAST(@Payload AS jsonb));
                """, new
            {
                EventId = Guid.NewGuid(),
                EventType = eventType,
                context.TenantId,
                AggregateId = eventType == "AuthorizationPolicyChanged" ? context.TenantId :
                    eventType == "TenantUserAccessChanged" ? subjectActorId : requestId,
                InitiatorActorId = context.ActorId,
                context.CorrelationId,
                PolicyVersion = policyVersion,
                SubjectVersion = eventType == "TenantUserAccessChanged" ? subjectVersion : null,
                Payload = payload
            }, transaction, ct)).ConfigureAwait(false);
    }

    private AccessRequestReviewItem Review(AccessRequestReviewRow row)
    {
        RequestTargets.TryGet(row.TargetCode, out var target);
        return new(row.RequestId, row.Requester, row.RequesterRole, row.TargetCode,
            target?.Label ?? "Retired access target", row.State == "Submitted" ? "UnderReview" : row.State,
            Utc(row.CreatedAt), Utc(row.ExpiresAt), NullableUtc(row.DecisionAt), SafeReason(row.DecisionReason),
            target?.GrantExplanation ?? "This target is no longer available and cannot be approved.");
    }

    private AccessRequestReviewItem Review(DecisionRow row)
    {
        RequestTargets.TryGet(row.TargetCode, out var target);
        return new(row.RequestId, row.Requester, row.RequesterRole, row.TargetCode,
            target?.Label ?? "Retired access target", row.State == "Submitted" ? "UnderReview" : row.State,
            Utc(row.CreatedAt), Utc(row.ExpiresAt), NullableUtc(row.DecisionAt), SafeReason(row.DecisionReason),
            target?.GrantExplanation ?? "This target is no longer available and cannot be approved.");
    }

    private static AccessRequestTargetStatus Status(
        AccessRequestTargetDefinition target, AccessRequestRow? row, bool hasAccess, bool canRequest) =>
        new(row?.RequestId, target.Code, target.Label, target.GrantExplanation,
            row?.State ?? "NotRequested", hasAccess, canRequest,
            row is null ? null : Utc(row.CreatedAt), NullableUtc(row?.DecisionAt), NullableUtc(row?.FulfilledAt),
            SafeReason(row?.DecisionReason));

    private static string? SafeReason(string? value) => value switch
    {
        "AdministratorRejected" => "Not approved by your Organization Administrator",
        "RequesterNoLongerEligible" => "The requester's access changed before review",
        "RequestExpired" => "The request expired before review",
        "CapabilityUnavailable" => "The requested access is no longer available",
        _ => null
    };

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(DateTime? value) => value is null ? null : Utc(value.Value);
    private static void OperationId(string value) =>
        AccessInput.Require(value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_OPERATION_ID");
    private static void RequestId(string value) =>
        AccessInput.Require(value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_ACCESS_REQUEST_ID");

    private sealed record BusinessSubjectRow(string? RoleId, long SubjectVersion, string Status, string? RoleCode);
    private sealed record PermissionDefinitionRow(string PermissionId, string PermissionCode, string CapabilityId, string Scope);
    private sealed record SubjectGrantRow(
        string PermissionId, string PermissionCode, string CapabilityId, string Scope,
        string SnapshotPermissionCode, string SnapshotCapabilityId, string SnapshotScope,
        string TargetCode, string TargetDefinitionHash, string RoleCode);
    private sealed record AccessRequestRow(
        string RequestId, string TargetCode, string TargetDefinitionHash, string State, DateTime CreatedAt,
        DateTime? DecisionAt, DateTime? FulfilledAt, string? DecisionReason);
    private sealed record AccessRequestReviewRow(
        string RequestId, string Requester, string RequesterRole, string TargetCode, string TargetDefinitionHash, string State,
        DateTime CreatedAt, DateTime ExpiresAt, DateTime? DecisionAt, string? DecisionReason);
    private sealed record DecisionRow(
        string RequestId, string RequesterActorId, string Requester, string RequesterRole,
        string TargetCode, string TargetDefinitionHash,
        string RequesterRoleId, long RequesterSubjectVersion, string State, DateTime CreatedAt, DateTime ExpiresAt,
        DateTime? DecisionAt, string? DecisionAction, string? DecisionOperationId, string? DecisionReason);
}
