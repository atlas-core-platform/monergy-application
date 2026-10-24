using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

// Only current, consent-authorized customer IDs. The client never supplies an
// actor or claims it is a customer owner. Display names are not exposed here.
public sealed record AdvisorAuthorizedCustomers(string TenantId, string ActorId,
    long PolicyVersion, long SubjectVersion, IReadOnlyList<string> CustomerIds);

public sealed partial class PostgresAccessAdministration
{
    public async Task<AdvisorAuthorizedCustomers> AdvisorCustomersAsync(TrustedActorContext context,
        CustomerAccessEvaluator evaluator, CancellationToken ct)
    {
        var before = await AdvisorContextAsync(context, ct).ConfigureAwait(false);
        string[] assigned;
        try
        {
            await using var db = await connections.OpenAsync(context.TenantId, ct).ConfigureAwait(false);
            // Bound the candidate set; never silently truncate the customer's access list.
            assigned = (await db.QueryAsync<string>(new CommandDefinition("""
                SELECT customer_id FROM am.customer_relationships
                WHERE actor_id=@ActorId ORDER BY customer_id LIMIT 51;
                """, new { context.ActorId }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        }
        catch (NpgsqlException ex) { throw new AccessStoreUnavailableException(ex); }
        AccessInput.Require(assigned.Length <= 50, "ADVISOR_RELATIONSHIP_LIMIT", 409);

        var eligible = new List<string>();
        foreach (var customerId in assigned)
        {
            var read = await evaluator.EvaluateAsync(context,
                new CustomerAccessRequest("financial-profile.profile.read", customerId, CustomerAccessPolicy.AdvicePurpose), ct)
                .ConfigureAwait(false);
            var evidence = await evaluator.EvaluateAsync(context,
                new CustomerAccessRequest("evidence.document.read", customerId, CustomerAccessPolicy.AdvicePurpose), ct)
                .ConfigureAwait(false);
            if (read.ReasonCode == "DependencyUnavailable" || evidence.ReasonCode == "DependencyUnavailable")
                throw new AccessStoreUnavailableException();
            if (read is { Outcome: "Allow", AccessBasis: "Advisor", ConsentVersion: > 0 } &&
                evidence is { Outcome: "Allow", AccessBasis: "Advisor", ConsentVersion: > 0 } &&
                read.PolicyVersion == before.PolicyVersion && evidence.PolicyVersion == before.PolicyVersion &&
                read.SubjectVersion == before.SubjectVersion && evidence.SubjectVersion == before.SubjectVersion)
                eligible.Add(customerId);
        }

        // A changed role, assignment or policy invalidates the whole result.
        var after = await AdvisorContextAsync(context, ct).ConfigureAwait(false);
        AccessInput.Require(before == after, "ADVISOR_CONTEXT_CHANGED", 409);
        return new(context.TenantId, context.ActorId, after.PolicyVersion, after.SubjectVersion, eligible);
    }
}
