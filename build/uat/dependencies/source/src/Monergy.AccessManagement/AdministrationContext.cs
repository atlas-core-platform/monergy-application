using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed record AdministrationContext(string TenantId, string ActorId, long PolicyVersion, string Authority);

public sealed partial class PostgresAccessAdministration
{
    public Task<AdministrationResult> RevokeSessionsAsync(TrustedActorContext context, string actorId, PolicyRevision revision, CancellationToken ct)
    {
        AccessInput.Identifier(actorId);
        return MutateAsync(context, revision.ExpectedPolicyVersion, actorId, "member.sessions-revoked", async (connection, transaction) =>
        {
            var version = await IncrementSubjectAsync(connection, transaction, actorId, ct).ConfigureAwait(false);
            AccessInput.Require(version > 0, "MEMBER_NOT_FOUND", 404);
            return new MutationEffect(version, actorId);
        }, ct);
    }

    // A current owner decision, never an authority supplied by the browser or cached by a peer.
    public async Task<AdministrationContext> ContextAsync(TrustedActorContext context, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var version = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;", null, transaction, ct)).ConfigureAwait(false);
            await RequireAdministratorAsync(connection, transaction, context.ActorId, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(context.TenantId, context.ActorId, version, "TenantAdmin");
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }
}
