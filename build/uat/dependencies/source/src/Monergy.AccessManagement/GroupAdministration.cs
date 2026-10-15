using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed record GroupWrite(long ExpectedPolicyVersion, string Code, string Label);
public sealed record GroupMembersWrite(long ExpectedPolicyVersion, string[] ActorIds);

public sealed partial class PostgresAccessAdministration
{
    public async Task<AdministrationPage> ListGroupMembersAsync(
        TrustedActorContext context, string groupId, string? after, int limit, CancellationToken ct)
    {
        AccessInput.Identifier(groupId);
        AccessInput.Require(limit is >= 1 and <= 100, "INVALID_PAGE_SIZE");
        if (!string.IsNullOrEmpty(after)) AccessInput.Identifier(after);
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var version = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            var exists = await connection.ExecuteScalarAsync<bool>(Command(
                "SELECT EXISTS (SELECT 1 FROM am.groups WHERE group_id=@Id);", new { Id = groupId }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(exists, "GROUP_NOT_FOUND", 404);
            var rows = (await connection.QueryAsync<DocumentRow>(Command("""
                SELECT actor_id AS Id,jsonb_build_object('actorId',actor_id)::text AS Document
                FROM am.group_members WHERE group_id=@Id AND actor_id>@After ORDER BY actor_id LIMIT @Limit;
                """, new { Id = groupId, After = after ?? "", Limit = limit + 1 }, transaction, ct)).ConfigureAwait(false)).ToArray();
            var items = rows.Take(limit).Select(row => JsonSerializer.Deserialize<JsonElement>(row.Document)).ToArray();
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(version, items, rows.Length > limit ? rows[limit - 1].Id : null);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }

    public Task<AdministrationResult> SaveGroupAsync(
        TrustedActorContext context, string? groupId, GroupWrite request, CancellationToken ct)
    {
        var code = AccessInput.Code(request.Code);
        var label = AccessInput.Label(request.Label);
        var id = groupId ?? Guid.NewGuid().ToString("N");
        AccessInput.Identifier(id);
        return MutateAsync(context, request.ExpectedPolicyVersion, id, "group.saved", async (connection, transaction) =>
        {
            var sql = groupId is null
                ? "INSERT INTO am.groups (group_id,code,label) VALUES (@Id,@Code,@Label);"
                : "UPDATE am.groups SET code=@Code,label=@Label WHERE group_id=@Id;";
            RequireFound(await connection.ExecuteAsync(Command(sql, new { Id = id, Code = code, Label = label }, transaction, ct)).ConfigureAwait(false));
            return new MutationEffect();
        }, ct);
    }

    public Task<AdministrationResult> SetGroupMembersAsync(
        TrustedActorContext context, string groupId, GroupMembersWrite request, CancellationToken ct)
    {
        AccessInput.Identifier(groupId);
        AccessInput.Require(request.ActorIds is { Length: <= 1000 }, "INVALID_GROUP_MEMBERS");
        foreach (var actorId in request.ActorIds) AccessInput.Identifier(actorId);
        AccessInput.Require(request.ActorIds.Distinct(StringComparer.Ordinal).Count() == request.ActorIds.Length, "DUPLICATE_GROUP_MEMBER");
        return MutateAsync(context, request.ExpectedPolicyVersion, groupId, "group.members-replaced", async (connection, transaction) =>
        {
            var exists = await connection.ExecuteScalarAsync<bool>(Command(
                "SELECT EXISTS (SELECT 1 FROM am.groups WHERE group_id=@Id);", new { Id = groupId }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(exists, "GROUP_NOT_FOUND", 404);
            var count = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT count(*) FROM am.access_subjects WHERE actor_id=ANY(@Ids);", new { Ids = request.ActorIds }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(count == request.ActorIds.Length, "MEMBER_NOT_FOUND", 404);
            await connection.ExecuteAsync(Command("DELETE FROM am.group_members WHERE group_id=@Id;",
                new { Id = groupId }, transaction, ct)).ConfigureAwait(false);
            await connection.ExecuteAsync(Command("""
                INSERT INTO am.group_members (group_id,actor_id) SELECT @Id,unnest(@Ids);
                """, new { Id = groupId, Ids = request.ActorIds }, transaction, ct)).ConfigureAwait(false);
            return new MutationEffect();
        }, ct);
    }
}
