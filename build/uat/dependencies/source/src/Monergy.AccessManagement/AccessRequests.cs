using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed record AccessRequestSubmission(string OperationId);
public sealed record AccessRequestDecision(string OperationId);
public sealed record AdvisorAccessRequestStatus(
    string TenantId,
    string ActorId,
    long PolicyVersion,
    long SubjectVersion,
    string? RequestId,
    string Target,
    string TargetLabel,
    string State,
    bool HasAccess,
    bool CanRequest,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DecisionAt,
    DateTimeOffset? FulfilledAt,
    string? DecisionReason);
public sealed record AccessRequestReviewItem(
    string RequestId,
    string Requester,
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
    private const string AdvisorTarget = "financial-advisor.assigned-customer-financial-review";
    private const string AdvisorTargetLabel = "View assigned customer financial information";
    private const string AdvisorPermissionId = "default-permission-profile-read";
    private const string AdvisorPermissionCode = "financial-profile-read";
    private const string AdvisorCapability = "financial-profile.profile.read";
    private const string AdvisorEvidencePermissionId = "default-permission-evidence-read";
    private const string AdvisorEvidencePermissionCode = "evidence-read";
    private const string AdvisorEvidenceCapability = "evidence.document.read";
    private const string AdvisorRoleCode = "financial-advisor";
    private const string GrantExplanation =
        "Allows this Financial Advisor to read financial information only for customers separately assigned to them, with the required record access and current customer consent.";

    public async Task<AdvisorAccessRequestStatus> ReadAdvisorAccessRequestAsync(
        TrustedActorContext context, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            var subject = await RequireAdvisorAsync(connection, transaction, context.ActorId, false, ct).ConfigureAwait(false);
            var available = await TargetAvailableAsync(connection, transaction, ct).ConfigureAwait(false);
            var hasAccess = available && await HasTargetAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
                SELECT request_id AS RequestId,state AS State,created_at AS CreatedAt,
                    decision_at AS DecisionAt,fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
                FROM am.access_requests
                WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND target_code=@Target
                ORDER BY created_at DESC,request_id DESC LIMIT 1;
                """, new { context.TenantId, context.ActorId, Target = AdvisorTarget }, transaction, ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Status(context, policyVersion, subject.SubjectVersion, row, hasAccess, available && !hasAccess &&
                (row is null || !string.Equals(row.State, "Submitted", StringComparison.Ordinal)));
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public async Task<AdvisorAccessRequestStatus> SubmitAdvisorAccessRequestAsync(
        TrustedActorContext context, AccessRequestSubmission request, CancellationToken ct)
    {
        OperationId(request.OperationId);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtext(@ActorId));",
                new { context.ActorId }, transaction, ct)).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            var subject = await RequireAdvisorAsync(connection, transaction, context.ActorId, true, ct).ConfigureAwait(false);
            AccessInput.Require(await TargetAvailableAsync(connection, transaction, ct).ConfigureAwait(false),
                "ACCESS_REQUEST_TARGET_UNAVAILABLE", 409);
            AccessInput.Require(!await HasTargetAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false),
                "ACCESS_ALREADY_AVAILABLE", 409);

            var replay = await connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
                SELECT request_id AS RequestId,state AS State,created_at AS CreatedAt,
                    decision_at AS DecisionAt,fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
                FROM am.access_requests
                WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND submit_operation_id=@OperationId;
                """, new { context.TenantId, context.ActorId, request.OperationId }, transaction, ct)).ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Status(context, policyVersion, subject.SubjectVersion, replay, false, false);
            }

            var existing = await connection.QuerySingleOrDefaultAsync<AccessRequestRow>(Command("""
                SELECT request_id AS RequestId,state AS State,created_at AS CreatedAt,
                    decision_at AS DecisionAt,fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
                FROM am.access_requests
                WHERE tenant_id=@TenantId AND requester_actor_id=@ActorId AND target_code=@Target AND state='Submitted';
                """, new { context.TenantId, context.ActorId, Target = AdvisorTarget }, transaction, ct)).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Status(context, policyVersion, subject.SubjectVersion, existing, false, false);
            }

            var id = Guid.NewGuid().ToString("N");
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.access_requests (
                    request_id,tenant_id,requester_actor_id,target_code,
                    requester_role_id,requester_subject_version,submit_operation_id,state,expires_at)
                VALUES (@RequestId,@TenantId,@ActorId,@Target,@RoleId,@SubjectVersion,
                    @OperationId,'Submitted',clock_timestamp()+interval '7 days');
                INSERT INTO am.access_request_events (
                    event_id,request_id,event_type,actor_id,policy_version)
                VALUES (@EventId,@RequestId,'Submitted',@ActorId,@PolicyVersion);
                """, new
            {
                RequestId = id,
                context.TenantId,
                ActorId = context.ActorId,
                Target = AdvisorTarget,
                subject.RoleId,
                subject.SubjectVersion,
                request.OperationId,
                EventId = Guid.NewGuid(),
                PolicyVersion = policyVersion
            }, transaction, ct)).ConfigureAwait(false);
            await WriteAccessRequestAuditAsync(connection, transaction, context, id, policyVersion, subject.SubjectVersion,
                "access-request.submitted", context.ActorId, ct).ConfigureAwait(false);
            var created = await connection.QuerySingleAsync<AccessRequestRow>(Command("""
                SELECT request_id AS RequestId,state AS State,created_at AS CreatedAt,
                    decision_at AS DecisionAt,fulfilled_at AS FulfilledAt,decision_reason AS DecisionReason
                FROM am.access_requests WHERE request_id=@RequestId;
                """, new { RequestId = id }, transaction, ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Status(context, policyVersion, subject.SubjectVersion, created, false, false);
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
                SELECT r.request_id AS RequestId,s.normalized_email AS Requester,r.state AS State,
                    r.created_at AS CreatedAt,r.expires_at AS ExpiresAt,r.decision_at AS DecisionAt,
                    r.decision_reason AS DecisionReason
                FROM am.access_requests r
                JOIN am.access_subjects s ON s.actor_id=r.requester_actor_id
                WHERE r.tenant_id=@TenantId AND (@After IS NULL OR
                    (r.created_at,r.request_id) < ((SELECT created_at FROM anchor),(SELECT request_id FROM anchor)))
                ORDER BY r.created_at DESC,r.request_id DESC LIMIT @Take;
                """, new { context.TenantId, After = after, Take = limit + 1 }, transaction, ct)).ConfigureAwait(false)).ToArray();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            var items = rows.Take(limit).Select(row => new AccessRequestReviewItem(
                row.RequestId, row.Requester, AdvisorTarget, AdvisorTargetLabel,
                row.State == "Submitted" ? "UnderReview" : row.State,
                Utc(row.CreatedAt), Utc(row.ExpiresAt), NullableUtc(row.DecisionAt),
                SafeReason(row.DecisionReason), GrantExplanation)).ToArray();
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
            var request = await connection.QuerySingleOrDefaultAsync<DecisionRow>(Command("""
                SELECT r.request_id AS RequestId,r.requester_actor_id AS RequesterActorId,
                    s.normalized_email AS Requester,r.requester_role_id AS RequesterRoleId,
                    r.requester_subject_version AS RequesterSubjectVersion,r.state AS State,
                    r.created_at AS CreatedAt,r.expires_at AS ExpiresAt,r.decision_at AS DecisionAt,
                    r.decision_action AS DecisionAction,r.decision_operation_id AS DecisionOperationId,
                    r.decision_reason AS DecisionReason
                FROM am.access_requests r
                JOIN am.access_subjects s ON s.actor_id=r.requester_actor_id
                WHERE r.tenant_id=@TenantId AND r.request_id=@RequestId AND r.target_code=@Target
                FOR UPDATE OF r;
                """, new { context.TenantId, RequestId = requestId, Target = AdvisorTarget }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(request is not null, "ACCESS_REQUEST_NOT_FOUND", 404);
            AccessInput.Require(request!.RequesterActorId != context.ActorId, "ACCESS_REQUEST_SELF_REVIEW_DENIED", 403);
            if (request.State != "Submitted")
            {
                AccessInput.Require(request.DecisionAction == (approve ? "Approve" : "Reject"),
                    "ACCESS_REQUEST_ALREADY_DECIDED", 409);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return Review(request);
            }

            var reason = approve
                ? await ApprovalFailureAsync(connection, transaction, request, ct).ConfigureAwait(false)
                : "AdministratorRejected";
            if (reason is not null)
            {
                await RejectAsync(connection, transaction, context, request, decision.OperationId,
                    approve ? "Approve" : "Reject", reason, policyVersion, ct).ConfigureAwait(false);
            }
            else
            {
                var alreadyGranted = await HasTargetAsync(connection, transaction, request.RequesterActorId, ct).ConfigureAwait(false);
                var nextPolicyVersion = policyVersion;
                long? subjectVersion = null;
                if (!alreadyGranted)
                {
                    await connection.ExecuteAsync(Command("""
                        INSERT INTO am.subject_permissions (
                            actor_id,permission_id,granted_at,granted_by,source_request_id)
                        VALUES (@ActorId,@ProfilePermissionId,clock_timestamp(),@GrantedBy,@RequestId),
                               (@ActorId,@EvidencePermissionId,clock_timestamp(),@GrantedBy,@RequestId);
                        """, new
                    {
                        ActorId = request.RequesterActorId,
                        ProfilePermissionId = AdvisorPermissionId,
                        EvidencePermissionId = AdvisorEvidencePermissionId,
                        GrantedBy = context.ActorId,
                        RequestId = request.RequestId
                    }, transaction, ct)).ConfigureAwait(false);
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
                    nextPolicyVersion, subjectVersion, "access-request.fulfilled", request.RequesterActorId, ct,
                    !alreadyGranted).ConfigureAwait(false);
            }

            var decided = await connection.QuerySingleAsync<DecisionRow>(Command("""
                SELECT r.request_id AS RequestId,r.requester_actor_id AS RequesterActorId,
                    s.normalized_email AS Requester,r.requester_role_id AS RequesterRoleId,
                    r.requester_subject_version AS RequesterSubjectVersion,r.state AS State,
                    r.created_at AS CreatedAt,r.expires_at AS ExpiresAt,r.decision_at AS DecisionAt,
                    r.decision_action AS DecisionAction,r.decision_operation_id AS DecisionOperationId,
                    r.decision_reason AS DecisionReason
                FROM am.access_requests r JOIN am.access_subjects s ON s.actor_id=r.requester_actor_id
                WHERE r.request_id=@RequestId;
                """, new { RequestId = request.RequestId }, transaction, ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return Review(decided);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new AccessAdministrationException("ACCESS_REQUEST_OPERATION_CONFLICT", 409);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private async Task<string?> ApprovalFailureAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DecisionRow request, CancellationToken ct)
    {
        if (!await TargetAvailableAsync(connection, transaction, ct).ConfigureAwait(false))
            return "CapabilityUnavailable";
        var expired = await connection.ExecuteScalarAsync<bool>(Command(
            "SELECT clock_timestamp()>=@ExpiresAt;", new { request.ExpiresAt }, transaction, ct)).ConfigureAwait(false);
        if (expired) return "RequestExpired";
        var current = await connection.QuerySingleOrDefaultAsync<AdvisorSubjectRow>(Command("""
            SELECT s.business_role_id AS RoleId,s.subject_version AS SubjectVersion,
                s.status AS Status,r.code AS RoleCode
            FROM am.access_subjects s LEFT JOIN am.roles r ON r.role_id=s.business_role_id
            WHERE s.actor_id=@ActorId FOR SHARE OF s;
            """, new { ActorId = request.RequesterActorId }, transaction, ct)).ConfigureAwait(false);
        return current is { Status: "Active", RoleCode: AdvisorRoleCode } &&
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
        await WriteAccessRequestAuditAsync(connection, transaction, context, request.RequestId, policyVersion,
            null, "access-request.rejected", request.RequesterActorId, ct).ConfigureAwait(false);
    }

    private static async Task<AdvisorSubjectRow> RequireAdvisorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, bool forShare, CancellationToken ct)
    {
        var subject = await connection.QuerySingleOrDefaultAsync<AdvisorSubjectRow>(Command($"""
            SELECT s.business_role_id AS RoleId,s.subject_version AS SubjectVersion,
                s.status AS Status,r.code AS RoleCode
            FROM am.access_subjects s JOIN am.roles r ON r.role_id=s.business_role_id
            WHERE s.actor_id=@ActorId {(forShare ? "FOR SHARE OF s,r" : string.Empty)};
            """, new { ActorId = actorId }, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(subject is { Status: "Active", RoleCode: AdvisorRoleCode }, "ADVISOR_ROLE_REQUIRED", 403);
        return subject!;
    }

    private async Task<bool> TargetAvailableAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (!catalog.TryGet(AdvisorCapability, out var capability) ||
            capability is not { Lifecycle: CapabilityLifecycle.Active, ResourceType: "customer" } ||
            !catalog.TryGet(AdvisorEvidenceCapability, out var evidence) ||
            evidence is not { Lifecycle: CapabilityLifecycle.Active, ResourceType: "document" }) return false;
        return await connection.ExecuteScalarAsync<bool>(Command("""
            SELECT (SELECT count(*) FROM am.permissions p
                JOIN am.permission_capabilities pc ON pc.permission_id=p.permission_id
                WHERE p.permission_id=@PermissionId AND p.code=@PermissionCode
                  AND pc.capability_id=@Capability AND pc.scope='Resource')=1
              AND (SELECT count(*) FROM am.permission_capabilities WHERE permission_id=@PermissionId)=1
              AND (SELECT count(*) FROM am.permissions p
                JOIN am.permission_capabilities pc ON pc.permission_id=p.permission_id
                WHERE p.permission_id=@EvidencePermissionId AND p.code=@EvidencePermissionCode
                  AND pc.capability_id=@EvidenceCapability AND pc.scope='Resource')=1
              AND (SELECT count(*) FROM am.permission_capabilities WHERE permission_id=@EvidencePermissionId)=1;
            """, new
        {
            PermissionId = AdvisorPermissionId,
            PermissionCode = AdvisorPermissionCode,
            Capability = AdvisorCapability,
            EvidencePermissionId = AdvisorEvidencePermissionId,
            EvidencePermissionCode = AdvisorEvidencePermissionCode,
            EvidenceCapability = AdvisorEvidenceCapability
        }, transaction, ct)).ConfigureAwait(false);
    }

    private static Task<bool> HasTargetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actorId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(Command("""
            SELECT count(DISTINCT capability_id)=2 FROM (
                SELECT pc.capability_id FROM am.access_subjects s
                JOIN am.role_permissions rp ON rp.role_id=s.business_role_id
                JOIN am.permission_capabilities pc ON pc.permission_id=rp.permission_id
                WHERE s.actor_id=@ActorId AND pc.capability_id=ANY(@Capabilities) AND pc.scope='Resource'
                UNION ALL
                SELECT pc.capability_id FROM am.subject_permissions sp
                JOIN am.permission_capabilities pc ON pc.permission_id=sp.permission_id
                WHERE sp.actor_id=@ActorId AND pc.capability_id=ANY(@Capabilities) AND pc.scope='Resource'
            ) grants;
            """, new
        {
            ActorId = actorId,
            Capabilities = new[] { AdvisorCapability, AdvisorEvidenceCapability }
        }, transaction, ct));

    private static async Task WriteAccessRequestAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TrustedActorContext context,
        string requestId, long policyVersion, long? subjectVersion, string operation,
        string subjectActorId, CancellationToken ct, bool authorizationChanged = false)
    {
        var payload = JsonSerializer.Serialize(new
        {
            operation,
            requestId,
            target = AdvisorTarget,
            subjectActorId
        });
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

    private static AdvisorAccessRequestStatus Status(
        TrustedActorContext context, long policyVersion, long subjectVersion,
        AccessRequestRow? row, bool hasAccess, bool canRequest) =>
        new(context.TenantId, context.ActorId, policyVersion, subjectVersion,
            row?.RequestId, AdvisorTarget, AdvisorTargetLabel, row?.State ?? "NotRequested", hasAccess, canRequest,
            row is null ? null : Utc(row.CreatedAt), NullableUtc(row?.DecisionAt), NullableUtc(row?.FulfilledAt),
            SafeReason(row?.DecisionReason));

    private static AccessRequestReviewItem Review(DecisionRow row) => new(
        row.RequestId, row.Requester, AdvisorTarget, AdvisorTargetLabel,
        row.State == "Submitted" ? "UnderReview" : row.State,
        Utc(row.CreatedAt), Utc(row.ExpiresAt), NullableUtc(row.DecisionAt),
        SafeReason(row.DecisionReason), GrantExplanation);

    private static string? SafeReason(string? value) => value switch
    {
        "AdministratorRejected" => "Not approved by your Organization Administrator",
        "RequesterNoLongerEligible" => "The requester's access changed before review",
        "RequestExpired" => "The request expired before review",
        "CapabilityUnavailable" => "The requested access is no longer available",
        _ => null
    };

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(DateTime? value) => value is null ? null : Utc(value.Value);
    private static void OperationId(string value) =>
        AccessInput.Require(value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_OPERATION_ID");
    private static void RequestId(string value) =>
        AccessInput.Require(value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_ACCESS_REQUEST_ID");

    private sealed record AdvisorSubjectRow(string? RoleId, long SubjectVersion, string Status, string? RoleCode);
    private sealed record AccessRequestRow(
        string RequestId, string State, DateTime CreatedAt, DateTime? DecisionAt, DateTime? FulfilledAt,
        string? DecisionReason);
    private sealed record AccessRequestReviewRow(
        string RequestId, string Requester, string State, DateTime CreatedAt, DateTime ExpiresAt,
        DateTime? DecisionAt, string? DecisionReason);
    private sealed record DecisionRow(
        string RequestId, string RequesterActorId, string Requester, string RequesterRoleId,
        long RequesterSubjectVersion, string State, DateTime CreatedAt, DateTime ExpiresAt,
        DateTime? DecisionAt, string? DecisionAction, string? DecisionOperationId, string? DecisionReason);
}
