using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

// This is only an authenticated workspace entry. Relationship count does not
// authorize reading any customer record or imply that Consent is active.
public sealed record AdvisorWorkspaceContext(
    string TenantId, string ActorId, string Authority, long PolicyVersion,
    long SubjectVersion, long RecordedRelationshipCount);

public sealed partial class PostgresAccessAdministration
{
    public async Task<AdvisorWorkspaceContext> AdvisorContextAsync(TrustedActorContext context, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var policyVersion = await connection.ExecuteScalarAsync<long>(Command(
                "SELECT policy_version FROM am.policy_state WHERE singleton FOR SHARE;",
                null, transaction, ct)).ConfigureAwait(false);
            var subjectVersion = await connection.ExecuteScalarAsync<long?>(Command("""
                SELECT s.subject_version FROM am.access_subjects s
                JOIN am.roles r ON r.role_id=s.business_role_id
                WHERE s.actor_id=@ActorId AND s.status='Active'
                  AND r.code='financial-advisor'
                FOR SHARE OF s,r;
                """, new { context.ActorId }, transaction, ct)).ConfigureAwait(false);
            AccessInput.Require(subjectVersion is > 0 && policyVersion > 0,
                "ADVISOR_ROLE_REQUIRED", 403);
            var relationships = await connection.ExecuteScalarAsync<long>(Command("""
                SELECT count(*) FROM am.customer_relationships
                WHERE actor_id=@ActorId;
                """, new { context.ActorId }, transaction, ct)).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(context.TenantId, context.ActorId, "FinancialAdvisor", policyVersion,
                subjectVersion!.Value, relationships);
        }
        catch (NpgsqlException exception) { throw new AccessStoreUnavailableException(exception); }
    }
}
