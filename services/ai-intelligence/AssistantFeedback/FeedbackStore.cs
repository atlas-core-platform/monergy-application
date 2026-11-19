using System.Data;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Monergy.Platform;
using Npgsql;

namespace Monergy.Services.AiIntelligence.AssistantFeedback;

public sealed class FeedbackStore(IConfiguration configuration)
{
    private string Connection => configuration["Monergy:AssistantFeedback:ConnectionString"] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Assistant feedback store unavailable.");

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(Connection);
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static CommandDefinition Sql(string sql, object? parameters = null, NpgsqlTransaction? tx = null, CancellationToken ct = default) =>
        new(sql, parameters, tx, cancellationToken: ct);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, AssistantContract.Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, AssistantContract.Json) ?? throw new JsonException();

    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<bool>(Sql("SELECT to_regclass('ai_feedback.topics') IS NOT NULL AND to_regclass('ai_feedback.audit_outbox') IS NOT NULL", ct: ct)).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is System.Data.Common.DbException or InvalidOperationException) { return false; }
    }

    public async Task<FeedbackSaved> SubmitAsync(HelpTopicSubmission request, CancellationToken ct)
    {
        // Identity-derived idempotency namespace is computed by the authenticated Gateway. No identity or session is stored.
        var digest = AssistantContract.Hash(Serialize(request));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, request.IdempotencyKey, digest, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        var now = DateTimeOffset.UtcNow;
        var topic = new HelpTopic(Guid.NewGuid().ToString(), request.GeneralQuestion.Trim(), request.SourceAudience,
            "Knowledge gap", "New", 1, now, now.AddDays(30), request.TopicId, null, null, false, null);
        await connection.ExecuteAsync(Sql("""
            INSERT INTO ai_feedback.topics(gap_id,source_audience,status,revision,created_at,expires_at,payload,knowledge_revision,retrieval_revision)
            VALUES(@id,@audience,'New',1,@created,@expires,@payload::jsonb,@knowledge,@retrieval)
            """, new { id = Guid.Parse(topic.GapId), audience = topic.SourceAudience, created = now, expires = topic.ExpiresAt,
                payload = Serialize(topic), knowledge = request.KnowledgeRevision, retrieval = request.RetrievalRevision }, transaction, ct)).ConfigureAwait(false);
        var saved = new FeedbackSaved(true, topic.GapId);
        await RecordOperationAsync(connection, transaction, request.IdempotencyKey, digest, saved, topic.ExpiresAt, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return saved;
    }

    public async Task CountAsync(FeedbackCounter counter, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql("""
            INSERT INTO ai_feedback.counters(day,source_audience,category,intent,knowledge_revision,retrieval_revision,count)
            VALUES((clock_timestamp() AT TIME ZONE 'UTC')::date,@SourceAudience,@Category,@Intent,@KnowledgeRevision,@RetrievalRevision,1)
            ON CONFLICT(day,source_audience,category,intent,knowledge_revision,retrieval_revision)
            DO UPDATE SET count=LEAST(ai_feedback.counters.count+1,9223372036854775806)
            """, counter, ct: ct)).ConfigureAwait(false);
    }

    public async Task<FeedbackPage> ReadAsync(FeedbackReviewRequest request, FeedbackAuthority authority, CancellationToken ct)
    {
        var operation = request.GapId is null ? "list" : "read";
        var decision = await authority.RequireAsync(request.AuthenticationContext, operation, request.GapId, ct).ConfigureAwait(false);
        if (request.Limit is < 1 or > 20 || request.Status is not null && !FeedbackRules.Statuses.Contains(request.Status, StringComparer.Ordinal) ||
            request.SourceAudience is not (null or "product" or "platform")) throw new TenantBoundaryException("FEEDBACK_QUERY_INVALID", 400);
        DateTimeOffset? afterTime = null; Guid? afterId = null;
        if (request.Cursor is not null)
        {
            var parts = request.Cursor.Split('|');
            if (parts.Length != 2 || !DateTimeOffset.TryParseExact(parts[0], "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var time) || !Guid.TryParseExact(parts[1], "D", out var id))
                throw new TenantBoundaryException("FEEDBACK_CURSOR_INVALID", 400);
            afterTime = time; afterId = id;
        }
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<TopicProjection>(Sql("""
            SELECT payload::text AS Payload,
             CASE WHEN payload->>'topicId' IS NOT NULL THEN
              (SELECT count(*) FROM ai_feedback.topics s WHERE s.payload->>'topicId'=t.payload->>'topicId'
               AND s.source_audience=t.source_audience AND s.knowledge_revision=t.knowledge_revision
               AND s.retrieval_revision=t.retrieval_revision AND s.deleted_at IS NULL AND s.expires_at>clock_timestamp()) END AS SubmissionCount,
             CASE WHEN payload->>'topicId' IS NOT NULL THEN
              (SELECT COALESCE(sum(c.count),0)::bigint FROM ai_feedback.counters c WHERE c.intent=t.payload->>'topicId'
               AND c.source_audience=t.source_audience AND c.knowledge_revision=t.knowledge_revision
               AND c.retrieval_revision=t.retrieval_revision) END AS OutcomeCount
            FROM ai_feedback.topics t WHERE deleted_at IS NULL AND expires_at>clock_timestamp()
             AND (@gap::uuid IS NULL OR gap_id=@gap) AND (@status::text IS NULL OR status=@status)
             AND (@audience::text IS NULL OR source_audience=@audience)
             AND (@after::timestamptz IS NULL OR (created_at,gap_id)>(@after,@afterId::uuid))
            ORDER BY created_at,gap_id LIMIT @limit
            """, new { gap = request.GapId is null ? (Guid?)null : Guid.Parse(request.GapId), status = request.Status,
                audience = request.SourceAudience, after = afterTime, afterId, limit = request.Limit + 1 }, ct: ct)).ConfigureAwait(false))
            .Select(row => Deserialize<HelpTopic>(row.Payload) with { TopicSubmissionCount = row.SubmissionCount, MatchedOutcomeCount = row.OutcomeCount }).ToArray();
        var topics = rows.Take(request.Limit).ToArray();
        using (var catalogue = new AssistantKnowledgeCatalogue(configuration["Monergy:AssistantFeedback:CataloguePath"] ?? "",
            configuration["Monergy:AssistantFeedback:CatalogueSha256"] ?? ""))
        {
            topics = [.. topics.Select(topic => topic.TopicId is not null && catalogue.TopicAllowed(topic.SourceAudience, topic.TopicId)
                ? topic : topic with { TopicSubmissionCount = null, MatchedOutcomeCount = null })];
        }
        decision = await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        await OutboxAsync(connection, transaction, Audit(decision, Guid.NewGuid().ToString(), "FeedbackRead", "ReleasePrepared", "ReleaseAuthorized", null, null), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        // Durable evidence describes an authorized release attempt, never a claim that a human read it.
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        return new(topics, rows.Length > request.Limit ? topics[^1].CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "|" + topics[^1].GapId : null);
    }

    public async Task<FeedbackSaved> ChangeAsync(FeedbackReviewRequest request, FeedbackAuthority authority, CancellationToken ct)
    {
        if (request.Change is not { } change || request.GapId is null || !AssistantContract.OpaqueId(request.GapId) ||
            request.OperationId is null || !AssistantContract.OpaqueId(request.OperationId)) throw new TenantBoundaryException("FEEDBACK_REVIEW_INVALID", 400);
        FeedbackRules.Change(change);
        var operation = change.PrivacyDelete ? "privacy-delete" : change.Resolution is not null ? "resolution" : "status";
        var decision = await authority.RequireAsync(request.AuthenticationContext, operation, request.GapId, ct).ConfigureAwait(false);
        var key = AssistantContract.Hash("review|" + decision.PrincipalId + "|" + request.OperationId);
        var digest = AssistantContract.Hash(request.GapId + Serialize(change));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        decision = await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        var replay = await ReplayAsync(connection, transaction, key, digest, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        var payload = await connection.QuerySingleOrDefaultAsync<string>(Sql("SELECT payload::text FROM ai_feedback.topics WHERE gap_id=@id AND deleted_at IS NULL AND expires_at>clock_timestamp() FOR UPDATE",
            new { id = Guid.Parse(request.GapId) }, transaction, ct)).ConfigureAwait(false) ?? throw new TenantBoundaryException("HELP_TOPIC_NOT_FOUND", 404);
        var original = Deserialize<HelpTopic>(payload);
        if (original.Revision != change.ExpectedRevision) throw new TenantBoundaryException("HELP_TOPIC_REVISION_CONFLICT", 409);
        if (change.DuplicateOf is not null)
        {
            if (change.DuplicateOf == original.GapId || !await connection.ExecuteScalarAsync<bool>(Sql("SELECT EXISTS(SELECT 1 FROM ai_feedback.topics WHERE gap_id=@id AND deleted_at IS NULL AND expires_at>clock_timestamp() AND status<>'Duplicate')",
                    new { id = Guid.Parse(change.DuplicateOf) }, transaction, ct)).ConfigureAwait(false))
                throw new TenantBoundaryException("HELP_TOPIC_DUPLICATE_INVALID", 400);
        }
        var status = change.Status;
        if (status == "Resolved" && (!change.ApprovedGeneralTopic || !ResolutionApproved(change.Resolution))) status = "Ready for verification";
        var expires = change.ApprovedGeneralTopic ? original.CreatedAt.AddDays(180) : original.CreatedAt.AddDays(30);
        var updated = original with { GeneralQuestion = change.PrivacyDelete ? "" : original.GeneralQuestion,
            Classification = change.Classification, Status = status, Revision = checked(original.Revision + 1), ExpiresAt = expires,
            TopicId = change.TopicId, OwnerReference = change.OwnerReference, DuplicateOf = change.DuplicateOf,
            ApprovedGeneralTopic = change.ApprovedGeneralTopic, Resolution = change.Resolution };
        await connection.ExecuteAsync(Sql("UPDATE ai_feedback.topics SET payload=@payload::jsonb,status=@status,revision=@revision,expires_at=@expires,deleted_at=CASE WHEN @deleted THEN clock_timestamp() ELSE NULL END WHERE gap_id=@id",
            new { payload = Serialize(updated), status, revision = updated.Revision, expires, deleted = change.PrivacyDelete, id = Guid.Parse(updated.GapId) }, transaction, ct)).ConfigureAwait(false);
        var action = change.PrivacyDelete ? "FeedbackPrivacyDelete" : change.DuplicateOf is not null ? "FeedbackDuplicate" :
            original.Classification != updated.Classification ? "FeedbackClassify" : original.OwnerReference != updated.OwnerReference ? "FeedbackAssign" : "FeedbackStatus";
        var audit = Audit(decision, request.OperationId, action, "Committed",
            change.PrivacyDelete ? "PrivacyDeletion" : "ReviewApplied", updated.Revision, updated.SourceAudience);
        await OutboxAsync(connection, transaction, audit, ct).ConfigureAwait(false);
        var saved = new FeedbackSaved(true, updated.GapId, "Pending");
        await RecordOperationAsync(connection, transaction, key, digest, saved, expires, ct, Guid.Parse(audit.EventId)).ConfigureAwait(false);
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return saved;
    }

    private bool ResolutionApproved(TopicResolution? resolution)
    {
        if (resolution is null || configuration["Monergy:AssistantFeedback:PublishedResolutionsPath"] is not { Length: > 0 } path) return false;
        // Only operator-deployed approved publication receipts establish resolution, never text entered by a reviewer.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 131072) return false;
        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (digest != configuration["Monergy:AssistantFeedback:PublishedResolutionsSha256"]) return false;
        using var catalogue = new AssistantKnowledgeCatalogue(configuration["Monergy:AssistantFeedback:CataloguePath"] ?? "",
            configuration["Monergy:AssistantFeedback:CatalogueSha256"] ?? "");
        var publication = JsonSerializer.Deserialize<ApprovedPublication>(bytes, AssistantContract.Json);
        return publication is { ContractVersion: "1.0.0", ApprovedChanges.Length: <= 1000 } &&
            publication.KnowledgeRevision == catalogue.KnowledgeRevision && publication.RetrievalRevision == catalogue.RetrievalRevision &&
            (resolution.PublishedVersion == catalogue.KnowledgeRevision || resolution.PublishedVersion == catalogue.RetrievalRevision) &&
            publication.ApprovedChanges.Contains(resolution);
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
        _ = await connection.ExecuteAsync(Sql("SELECT pg_advisory_xact_lock(7101201)", tx: transaction, ct: ct)).ConfigureAwait(false);

    private static async Task<FeedbackSaved?> ReplayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string digest, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<OperationRow>(Sql("SELECT o.payload_hash AS Hash,o.result::text AS Result,a.delivered_at IS NOT NULL AS Delivered,o.expires_at<=clock_timestamp() AS Expired FROM ai_feedback.operations o LEFT JOIN ai_feedback.audit_outbox a ON a.event_id=o.audit_event_id WHERE operation_key=@key", new { key }, transaction, ct)).ConfigureAwait(false);
        if (row is null) return null;
        if (row.Expired) throw new TenantBoundaryException("HELP_TOPIC_OPERATION_EXPIRED", 409);
        if (row.Hash != digest) throw new TenantBoundaryException("HELP_TOPIC_IDEMPOTENCY_CONFLICT", 409);
        var result = Deserialize<FeedbackSaved>(row.Result);
        return result.AuditState == "Pending" && row.Delivered ? result with { AuditState = "Accepted" } : result;
    }
    private static async Task RecordOperationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string digest, FeedbackSaved result, DateTimeOffset expires, CancellationToken ct, Guid? auditId = null) =>
        _ = await connection.ExecuteAsync(Sql("INSERT INTO ai_feedback.operations(operation_key,payload_hash,gap_id,result,audit_event_id,expires_at) VALUES(@key,@digest,@id,@result::jsonb,@auditId,@expires)",
            new { key, digest, id = Guid.Parse(result.GapId), result = Serialize(result), auditId, expires }, transaction, ct)).ConfigureAwait(false);

    private static PlatformAuditEnvelope Audit(PlatformReviewDecision decision, string operationId, string action, string outcome, string reason, long? version, string? audience) =>
        new("1.0.0", Guid.NewGuid().ToString(), operationId, AssistantContract.Realm, "ai-feedback", "PlatformPrincipal", decision.PrincipalId!,
            AssistantContract.Capability, decision.DecisionId, decision.PolicyRevision, decision.GrantRevision, version, action, outcome, reason,
            decision.ResourceId, audience, DateTimeOffset.UtcNow);
    private static async Task OutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PlatformAuditEnvelope envelope, CancellationToken ct)
    {
        var capacity = envelope.ReasonCode == "DeliveryRetry" ? 10064 : 10000;
        if (await connection.ExecuteScalarAsync<long>(Sql("SELECT count(*) FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL", tx: transaction, ct: ct)).ConfigureAwait(false) >= capacity)
            throw new TenantBoundaryException("FEEDBACK_AUDIT_CAPACITY", 503);
        await connection.ExecuteAsync(Sql("INSERT INTO ai_feedback.audit_outbox(event_id,payload) VALUES(@id,@payload::jsonb)",
            new { id = Guid.Parse(envelope.EventId), payload = Serialize(envelope) }, transaction, ct)).ConfigureAwait(false);
    }

    internal async Task DeliverAsync(HttpClient client, CancellationToken ct)
    {
        var token = configuration["Monergy:AssistantFeedback:AuditToken"];
        if (token is not { Length: >= 32 }) throw new InvalidOperationException("Platform audit transport unavailable.");
        var destination = configuration["Monergy:AssistantFeedback:AuditUrl"] ?? "http://127.0.0.1:5102/";
        if (destination.TrimEnd('/') != "http://127.0.0.1:5102") throw new InvalidOperationException("Platform audit destination invalid.");
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<DeliveryRow>(Sql("SELECT event_id AS Id,payload::text AS Payload,attempt_count AS Attempts FROM ai_feedback.audit_outbox WHERE delivered_at IS NULL AND attempt_count<16 AND next_attempt_at<=clock_timestamp() ORDER BY created_at LIMIT 20 FOR UPDATE SKIP LOCKED", tx: transaction, ct: ct)).ConfigureAwait(false)).ToArray();
        foreach (var row in rows)
        {
            PlatformAuditReceipt? receipt = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, destination.TrimEnd('/') + "/internal/platform-audit/v1/events");
                request.Headers.Add("X-Monergy-Platform-Audit", token);
                request.Content = new StringContent(row.Payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var value = await response.Content.ReadFromJsonAsync<PlatformAuditReceipt>(AssistantContract.Json, ct).ConfigureAwait(false);
                    if (value is not null && value.EventId == row.Id.ToString() && value.State == "Accepted" && AssistantContract.OpaqueId(value.ReceiptId)) receipt = value;
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or JsonException or TaskCanceledException) { /* Durable pending state is the retry queue. */ }
            await connection.ExecuteAsync(Sql("UPDATE ai_feedback.audit_outbox SET attempt_count=LEAST(attempt_count+1,1000000),next_attempt_at=clock_timestamp()+@delay*interval '1 second',receipt=@receipt::jsonb,delivered_at=CASE WHEN @delivered THEN clock_timestamp() ELSE NULL END WHERE event_id=@id",
                new { delay = Math.Min(3600, 5 * (1 << Math.Min(row.Attempts, 9))), receipt = receipt is null ? null : Serialize(receipt), delivered = receipt is not null, id = row.Id }, transaction, ct)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    internal async Task RetainAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        // Operational deletion does not promise removal from separately governed backups.
        await connection.ExecuteAsync(Sql("""
            DELETE FROM ai_feedback.topics WHERE expires_at<=clock_timestamp();
            DELETE FROM ai_feedback.operations WHERE expires_at<=clock_timestamp();
            DELETE FROM ai_feedback.counters WHERE day<=(clock_timestamp() AT TIME ZONE 'UTC')::date-90;
            SELECT ai_feedback.purge_delivered_audit();
            """, tx: transaction, ct: ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<FeedbackDeliveryStatus> DeliveryStatusAsync(FeedbackReviewRequest request, FeedbackAuthority authority, bool retry, CancellationToken ct)
    {
        if (request.GapId is null || !AssistantContract.OpaqueId(request.GapId) || request.Change is not null ||
            request.Cursor is not null || request.Status is not null || request.SourceAudience is not null ||
            retry && (request.OperationId is null || !AssistantContract.OpaqueId(request.OperationId)))
            throw new TenantBoundaryException("FEEDBACK_DELIVERY_REQUEST_INVALID", 400);
        var decision = await authority.RequireAsync(request.AuthenticationContext, "status", request.GapId, ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(connection, transaction, ct).ConfigureAwait(false);
        decision = await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        if (!await connection.ExecuteScalarAsync<bool>(Sql("SELECT EXISTS(SELECT 1 FROM ai_feedback.topics WHERE gap_id=@id::uuid) OR EXISTS(SELECT 1 FROM ai_feedback.audit_outbox WHERE payload->>'resourceId'=@id)",
            new { id = request.GapId }, transaction, ct)).ConfigureAwait(false)) throw new TenantBoundaryException("HELP_TOPIC_NOT_FOUND", 404);
        if (retry)
        {
            var key = AssistantContract.Hash("delivery|" + decision.PrincipalId + "|" + request.OperationId);
            var digest = AssistantContract.Hash(request.GapId);
            var replay = await ReplayAsync(connection, transaction, key, digest, ct).ConfigureAwait(false);
            if (replay is null)
            {
                await connection.ExecuteAsync(Sql("UPDATE ai_feedback.audit_outbox SET attempt_count=0,recovery_count=recovery_count+1,next_attempt_at=clock_timestamp() WHERE payload->>'resourceId'=@id AND delivered_at IS NULL AND attempt_count>=16 AND recovery_count<3",
                    new { id = request.GapId }, transaction, ct)).ConfigureAwait(false);
                var audit = Audit(decision, request.OperationId!, "FeedbackStatus", "Committed", "DeliveryRetry", null, null);
                await OutboxAsync(connection, transaction, audit, ct).ConfigureAwait(false);
                await RecordOperationAsync(connection, transaction, key, digest, new(true, request.GapId, "Pending"), DateTimeOffset.UtcNow.AddDays(30), ct, Guid.Parse(audit.EventId)).ConfigureAwait(false);
            }
        }
        else await OutboxAsync(connection, transaction, Audit(decision, Guid.NewGuid().ToString(), "FeedbackRead", "ReleasePrepared", "ReleaseAuthorized", null, null), ct).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<FeedbackDelivery>(Sql("SELECT event_id::text AS EventId,attempt_count AS Attempts,recovery_count AS Recoveries,CASE WHEN delivered_at IS NOT NULL THEN 'Accepted' WHEN attempt_count>=16 THEN 'Paused' ELSE 'Pending' END AS State FROM ai_feedback.audit_outbox WHERE payload->>'resourceId'=@id ORDER BY created_at DESC LIMIT 50",
            new { id = request.GapId }, transaction, ct)).ConfigureAwait(false)).ToArray();
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await authority.ReconfirmAsync(decision, request.AuthenticationContext, ct).ConfigureAwait(false);
        return new(request.GapId, rows, 16, 3);
    }
    private sealed record OperationRow(string Hash, string Result, bool Delivered, bool Expired);
    private sealed record DeliveryRow(Guid Id, string Payload, int Attempts);
    private sealed record TopicProjection(string Payload, long? SubmissionCount, long? OutcomeCount);
}
