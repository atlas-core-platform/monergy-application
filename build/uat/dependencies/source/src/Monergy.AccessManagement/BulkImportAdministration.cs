using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed partial class PostgresAccessAdministration
{
    public Task<ImportPreview> PreviewImportAsync(TrustedActorContext context, ImportPreviewRequest request, CancellationToken ct)
    {
        var parsed = BulkImportCsv.Parse(request.Csv);
        return ImportTransactionAsync(context, async (connection, transaction, version) =>
        {
            var validated = await ValidateImportAsync(connection, transaction, parsed, ct).ConfigureAwait(false);
            return new ImportPreview(version, parsed.ContentHash, parsed.RowCount, validated.Errors);
        }, ct);
    }

    public Task<ImportResult> StageImportAsync(TrustedActorContext context, string importId, ImportCommitRequest request, CancellationToken ct)
    {
        AccessInput.Identifier(importId, 80);
        AccessInput.Require(request.ExpectedPolicyVersion >= 1, "INVALID_POLICY_VERSION");
        var parsed = BulkImportCsv.Parse(request.Csv);
        AccessInput.Require(parsed.ContentHash == request.ContentHash, "IMPORT_CONTENT_HASH_MISMATCH", 409);
        return ImportTransactionAsync(context, async (connection, transaction, version) =>
        {
            var existing = await FindBatchAsync(connection, transaction, importId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                AccessInput.Require(existing.ContentHash == parsed.ContentHash, "IMPORT_ID_CONTENT_CONFLICT", 409);
                return await ReadImportResultAsync(connection, transaction, existing, version, ct).ConfigureAwait(false);
            }
            RequireRevision(version, request.ExpectedPolicyVersion);
            var validated = await ValidateImportAsync(connection, transaction, parsed, ct).ConfigureAwait(false);
            AccessInput.Require(validated.Errors.Count == 0, "IMPORT_VALIDATION_FAILED", 400);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.import_batches (import_id,content_hash,status,row_count,created_by)
                VALUES (@Id,@ContentHash,'PendingIdentity',@RowCount,@ActorId);
                """, new { Id = importId, parsed.ContentHash, parsed.RowCount, context.ActorId }, transaction, ct)).ConfigureAwait(false);
            foreach (var row in validated.Rows)
            {
                var key = BulkImportCsv.Hash(JsonSerializer.Serialize(new { context.TenantId, importId, parsed.ContentHash, row.Number }));
                await connection.ExecuteAsync(Command("""
                    INSERT INTO am.import_rows (import_id,row_number,normalized_email,business_role_id,group_ids,identity_key)
                    VALUES (@Id,@Number,@Email,@RoleId,@GroupIds,@Key);
                    INSERT INTO am.import_email_reservations (normalized_email,import_id) VALUES (@Email,@Id);
                    """, new { Id = importId, row.Number, row.Email, row.RoleId, row.GroupIds, Key = key }, transaction, ct)).ConfigureAwait(false);
            }
            var nextVersion = await RecordImportChangeAsync(connection, transaction, context, importId, "import.staged", ct).ConfigureAwait(false);
            var batch = await RequireBatchAsync(connection, transaction, importId, ct).ConfigureAwait(false);
            return await ReadImportResultAsync(connection, transaction, batch, nextVersion, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<ImportResult> GetImportAsync(TrustedActorContext context, string importId, CancellationToken ct)
    {
        AccessInput.Identifier(importId, 80);
        return ImportTransactionAsync(context, async (connection, transaction, version) =>
        {
            var batch = await RequireBatchAsync(connection, transaction, importId, ct).ConfigureAwait(false);
            return await ReadImportResultAsync(connection, transaction, batch, version, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<ImportResult> CancelImportAsync(TrustedActorContext context, string importId, PolicyRevision request, CancellationToken ct)
    {
        AccessInput.Identifier(importId, 80);
        AccessInput.Require(request.ExpectedPolicyVersion >= 1, "INVALID_POLICY_VERSION");
        return ImportTransactionAsync(context, async (connection, transaction, version) =>
        {
            var batch = await RequireBatchAsync(connection, transaction, importId, ct).ConfigureAwait(false);
            if (batch.Status == "Cancelled") return await ReadImportResultAsync(connection, transaction, batch, version, ct).ConfigureAwait(false);
            RequireRevision(version, request.ExpectedPolicyVersion);
            AccessInput.Require(batch.Status != "Activated", "ACTIVATED_IMPORT_CANNOT_CANCEL", 409);
            await connection.ExecuteAsync(Command("""
                UPDATE am.import_batches SET status='Cancelled',error_code=NULL WHERE import_id=@Id;
                DELETE FROM am.import_email_reservations WHERE import_id=@Id;
                """, new { Id = importId }, transaction, ct)).ConfigureAwait(false);
            var nextVersion = await RecordImportChangeAsync(connection, transaction, context, importId, "import.cancelled", ct).ConfigureAwait(false);
            return await ReadImportResultAsync(connection, transaction, batch with { Status = "Cancelled", ErrorCode = null }, nextVersion, ct).ConfigureAwait(false);
        }, ct);
    }

    // Explicit local coordinator: each provision call is outside a SQL transaction.
    // Session lock excludes simultaneous workers for this import; cancellation is
    // allowed and checked again before every receipt and before activation.
    public async Task<ImportResult> ProcessImportAsync(TrustedActorContext context, string importId, PolicyRevision request, CancellationToken ct)
    {
        AccessInput.Identifier(importId, 80);
        AccessInput.Require(request.ExpectedPolicyVersion >= 1, "INVALID_POLICY_VERSION");
        if (provisioner is null) throw new AccessAdministrationException("IDENTITY_PROVISIONER_UNAVAILABLE", 503);
        await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
        var locked = false;
        try
        {
            locked = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(hashtextextended('am-import:' || @Id,0));",
                new { Id = importId }, cancellationToken: ct)).ConfigureAwait(false);
            AccessInput.Require(locked, "IMPORT_IN_PROGRESS", 409);
            var state = await ImportTransactionAsync(context, async (db, transaction, version) =>
            {
                var batch = await RequireBatchAsync(db, transaction, importId, ct).ConfigureAwait(false);
                if (batch.Status != "Activated")
                {
                    RequireRevision(version, request.ExpectedPolicyVersion);
                    RequirePending(batch);
                    await db.ExecuteAsync(Command("""
                        UPDATE am.import_batches SET status='PendingIdentity',error_code=NULL,attempts=attempts+1 WHERE import_id=@Id;
                        """, new { Id = importId }, transaction, ct)).ConfigureAwait(false);
                    await ImportEventAsync(db, transaction, context, importId, "import.attempt-started", version, ct).ConfigureAwait(false);
                }
                return new ImportWork(batch, await ReadImportRowsAsync(db, transaction, importId, ct).ConfigureAwait(false), version);
            }, ct, connection).ConfigureAwait(false);
            if (state.Batch.Status == "Activated") return Result(state.Batch, state.Rows, state.Version);

            try
            {
                foreach (var row in state.Rows.Where(row => row.ActorId is null))
                {
                    // Re-check current administrative authority and batch status
                    // even when resuming after an earlier remote operation.
                    await ImportTransactionAsync(context, async (db, transaction, version) =>
                    {
                        RequireRevision(version, request.ExpectedPolicyVersion);
                        RequirePending(await RequireBatchAsync(db, transaction, importId, ct).ConfigureAwait(false));
                        return true;
                    }, ct, connection).ConfigureAwait(false);
                    var receipt = await provisioner.ProvisionAsync(new(context.TenantId, importId, row.RowNumber,
                        row.NormalizedEmail, row.IdentityKey), ct).ConfigureAwait(false);
                    AccessInput.Require(receipt is not null && receipt.TenantId == context.TenantId &&
                        receipt.NormalizedEmail == row.NormalizedEmail && receipt.IdempotencyKey == row.IdentityKey,
                        "IDENTITY_RECEIPT_MISMATCH", 409);
                    AccessInput.Identifier(receipt!.ActorId);
                    await ImportTransactionAsync(context, async (db, transaction, version) =>
                    {
                        RequireRevision(version, request.ExpectedPolicyVersion);
                        RequirePending(await RequireBatchAsync(db, transaction, importId, ct).ConfigureAwait(false));
                        await db.ExecuteAsync(Command("""
                            UPDATE am.import_rows SET actor_id=@ActorId WHERE import_id=@Id AND row_number=@RowNumber;
                            """, new { Id = importId, row.RowNumber, receipt.ActorId }, transaction, ct)).ConfigureAwait(false);
                        return true;
                    }, ct, connection).ConfigureAwait(false);
                }
                return await ImportTransactionAsync(context, async (db, transaction, version) =>
                {
                    RequireRevision(version, request.ExpectedPolicyVersion);
                    var batch = await RequireBatchAsync(db, transaction, importId, ct).ConfigureAwait(false);
                    RequirePending(batch);
                    var rows = await ReadImportRowsAsync(db, transaction, importId, ct).ConfigureAwait(false);
                    AccessInput.Require(rows.Length == batch.RowCount && rows.All(row => row.ActorId is not null), "IDENTITIES_INCOMPLETE", 409);
                    // All inserts, group assignments, revision changes, outbox
                    // evidence and the Activated marker share this transaction.
                    foreach (var row in rows)
                    {
                        await db.ExecuteAsync(Command("""
                            INSERT INTO am.access_subjects (actor_id,normalized_email,status,business_role_id)
                            VALUES (@ActorId,@NormalizedEmail,'Active',@BusinessRoleId);
                            INSERT INTO am.group_members (group_id,actor_id) SELECT unnest(@GroupIds),@ActorId;
                            """, new { row.ActorId, row.NormalizedEmail, row.BusinessRoleId, row.GroupIds }, transaction, ct)).ConfigureAwait(false);
                    }
                    var nextVersion = await RecordImportChangeAsync(db, transaction, context, importId, "import.activated", ct).ConfigureAwait(false);
                    foreach (var row in rows)
                        await ImportEventAsync(db, transaction, context, importId, "member.imported", nextVersion, ct,
                            "TenantUserAccessChanged", row.ActorId, 1).ConfigureAwait(false);
                    await db.ExecuteAsync(Command("""
                        UPDATE am.import_batches SET status='Activated',error_code=NULL,activated_policy_version=@Version WHERE import_id=@Id;
                        DELETE FROM am.import_email_reservations WHERE import_id=@Id;
                        """, new { Id = importId, Version = nextVersion }, transaction, ct)).ConfigureAwait(false);
                    return Result(batch with { Status = "Activated", ErrorCode = null, ActivatedPolicyVersion = nextVersion }, rows, nextVersion);
                }, ct, connection).ConfigureAwait(false);
            }
            catch (IdentityProvisioningUnavailableException)
            {
                return await FailImportAsync(context, connection, importId, "IDENTITY_PROVISIONING_UNAVAILABLE", ct).ConfigureAwait(false);
            }
            catch (AccessAdministrationException exception) when (exception.StatusCode != 403)
            {
                return await FailImportAsync(context, connection, importId, exception.Code, ct).ConfigureAwait(false);
            }
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
        finally
        {
            if (locked && connection.State == System.Data.ConnectionState.Open)
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_unlock(hashtextextended('am-import:' || @Id,0));", new { Id = importId },
                    cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private Task<ImportResult> FailImportAsync(TrustedActorContext context, NpgsqlConnection connection, string importId, string errorCode, CancellationToken ct) =>
        ImportTransactionAsync(context, async (db, transaction, version) =>
        {
            var batch = await RequireBatchAsync(db, transaction, importId, ct).ConfigureAwait(false);
            if (batch.Status is "Cancelled" or "Activated") return await ReadImportResultAsync(db, transaction, batch, version, ct).ConfigureAwait(false);
            await db.ExecuteAsync(Command("UPDATE am.import_batches SET status='Failed',error_code=@ErrorCode WHERE import_id=@Id;",
                new { Id = importId, ErrorCode = errorCode }, transaction, ct)).ConfigureAwait(false);
            await ImportEventAsync(db, transaction, context, importId, "import.failed", version, ct).ConfigureAwait(false);
            return await ReadImportResultAsync(db, transaction, batch with { Status = "Failed", ErrorCode = errorCode }, version, ct).ConfigureAwait(false);
        }, ct, connection);

    private async Task<T> ImportTransactionAsync<T>(TrustedActorContext context,
        Func<NpgsqlConnection, NpgsqlTransaction, long, Task<T>> action, CancellationToken ct, NpgsqlConnection? existingConnection = null)
    {
        try
        {
            await using var ownedConnection = existingConnection is null ? await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false) : null;
            var connection = existingConnection ?? ownedConnection!;
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var version = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR UPDATE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var result = await action(connection, transaction, version).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation)
        {
            throw new AccessAdministrationException("POLICY_REFERENCE_CONFLICT", 409);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw new AccessAdministrationException("INVALID_IMPORT_DATA", 400);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    private static async Task<ValidatedImport> ValidateImportAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, BulkImportCsv.ParsedImport parsed, CancellationToken ct)
    {
        var errors = new List<ImportError>(parsed.Errors);
        var roles = (await connection.QueryAsync<CodeReference>(Command(
            "SELECT code AS Code,role_id AS Id FROM am.roles;", null, transaction, ct)).ConfigureAwait(false))
            .ToDictionary(row => row.Code, row => row.Id, StringComparer.Ordinal);
        var groups = (await connection.QueryAsync<CodeReference>(Command(
            "SELECT code AS Code,group_id AS Id FROM am.groups;", null, transaction, ct)).ConfigureAwait(false))
            .ToDictionary(row => row.Code, row => row.Id, StringComparer.Ordinal);
        var unavailable = (await connection.QueryAsync<string>(Command("""
            SELECT normalized_email FROM am.access_subjects WHERE normalized_email=ANY(@Emails)
            UNION SELECT normalized_email FROM am.import_email_reservations WHERE normalized_email=ANY(@Emails);
            """, new { Emails = parsed.Rows.Select(row => row.Email).ToArray() }, transaction, ct)).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var rows = new List<ResolvedImportRow>();
        foreach (var row in parsed.Rows)
        {
            if (unavailable.Contains(row.Email)) errors.Add(new(row.Number, "email", "EMAIL_ALREADY_REGISTERED_OR_RESERVED"));
            string? roleId = null;
            if (row.RoleCode.Length > 0 && !roles.TryGetValue(row.RoleCode, out roleId)) errors.Add(new(row.Number, "role_code", "ROLE_NOT_FOUND"));
            var groupIds = new List<string>();
            foreach (var code in row.GroupCodes)
            {
                if (groups.TryGetValue(code, out var id)) groupIds.Add(id);
                else errors.Add(new(row.Number, "group_codes", "GROUP_NOT_FOUND"));
            }
            rows.Add(new(row.Number, row.Email, roleId, groupIds.ToArray()));
        }
        return new(rows, errors.Distinct().OrderBy(error => error.RowNumber).ThenBy(error => error.Field, StringComparer.Ordinal).ToArray());
    }

    private static async Task<long> RecordImportChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TrustedActorContext context, string importId, string operation, CancellationToken ct)
    {
        var version = await connection.ExecuteScalarAsync<long>(Command(
            "UPDATE am.policy_state SET policy_version=policy_version+1 WHERE singleton RETURNING policy_version;", null, transaction, ct)).ConfigureAwait(false);
        await ImportEventAsync(connection, transaction, context, importId, operation, version, ct, "AuthorizationPolicyChanged").ConfigureAwait(false);
        await ImportEventAsync(connection, transaction, context, importId, operation, version, ct).ConfigureAwait(false);
        return version;
    }

    private static Task<int> ImportEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TrustedActorContext context,
        string importId, string operation, long version, CancellationToken ct, string eventType = "AccessManagementAuditEvidence", string? actorId = null, long? subjectVersion = null) =>
        connection.ExecuteAsync(Command("""
            INSERT INTO am.outbox (event_id,event_type,tenant_id,aggregate_id,initiator_actor_id,correlation_id,policy_version,subject_version,payload)
            VALUES (@EventId,@EventType,@TenantId,@AggregateId,@ActorId,@CorrelationId,@Version,@SubjectVersion,CAST(@Payload AS jsonb));
            """, new { EventId = Guid.NewGuid(), EventType = eventType, context.TenantId,
                AggregateId = eventType == "AuthorizationPolicyChanged" ? context.TenantId : actorId ?? importId,
                context.ActorId, context.CorrelationId, Version = version, SubjectVersion = subjectVersion,
                Payload = JsonSerializer.Serialize(new { operation, importId, subjectActorId = actorId }) }, transaction, ct));

    private static Task<ImportBatch?> FindBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ImportBatch>(Command("""
            SELECT import_id AS Id,content_hash AS ContentHash,status AS Status,row_count AS RowCount,
                activated_policy_version AS ActivatedPolicyVersion,error_code AS ErrorCode FROM am.import_batches WHERE import_id=@Id;
            """, new { Id = id }, transaction, ct));

    private static async Task<ImportBatch> RequireBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, CancellationToken ct) =>
        await FindBatchAsync(connection, transaction, id, ct).ConfigureAwait(false) ?? throw new AccessAdministrationException("IMPORT_NOT_FOUND", 404);

    private static async Task<ImportRow[]> ReadImportRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string id, CancellationToken ct) =>
        (await connection.QueryAsync<ImportRow>(Command("""
            SELECT row_number AS RowNumber,normalized_email AS NormalizedEmail,business_role_id AS BusinessRoleId,
                group_ids AS GroupIds,identity_key AS IdentityKey,actor_id AS ActorId FROM am.import_rows WHERE import_id=@Id ORDER BY row_number;
            """, new { Id = id }, transaction, ct)).ConfigureAwait(false)).ToArray();

    private static async Task<ImportResult> ReadImportResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ImportBatch batch, long version, CancellationToken ct) =>
        Result(batch, await ReadImportRowsAsync(connection, transaction, batch.Id, ct).ConfigureAwait(false), version);

    private static ImportResult Result(ImportBatch batch, ImportRow[] rows, long version) =>
        new(batch.Id, batch.ContentHash, batch.Status, batch.RowCount, version, batch.ActivatedPolicyVersion, batch.ErrorCode,
            rows.Select(row => new ImportIdentityState(row.RowNumber, row.NormalizedEmail, row.ActorId)).ToArray());
    private static void RequireRevision(long current, long expected) => AccessInput.Require(expected >= 1 && expected == current, "POLICY_VERSION_CONFLICT", 409);
    private static void RequirePending(ImportBatch batch) => AccessInput.Require(batch.Status is "PendingIdentity" or "Failed", "IMPORT_NOT_PENDING", 409);
    private sealed record CodeReference(string Code, string Id);
    private sealed record ResolvedImportRow(int Number, string Email, string? RoleId, string[] GroupIds);
    private sealed record ValidatedImport(List<ResolvedImportRow> Rows, IReadOnlyList<ImportError> Errors);
    private sealed record ImportBatch(string Id, string ContentHash, string Status, int RowCount, long? ActivatedPolicyVersion, string? ErrorCode);
    // Npgsql exposes a PostgreSQL array's field type as System.Array. Property
    // mapping avoids constructor-signature matching against string[].
    private sealed class ImportRow
    {
        public int RowNumber { get; set; }
        public string NormalizedEmail { get; set; } = "";
        public string? BusinessRoleId { get; set; }
        public string[] GroupIds { get; set; } = [];
        public string IdentityKey { get; set; } = "";
        public string? ActorId { get; set; }
    }
    private sealed record ImportWork(ImportBatch Batch, ImportRow[] Rows, long Version);
}
