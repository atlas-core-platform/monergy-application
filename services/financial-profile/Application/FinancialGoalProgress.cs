using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.FinancialProfile.Application;

public sealed class FinancialGoalProgressApplication(
    IFinancialGoalRepository goals,
    IGoalContributionRepository contributions,
    TimeProvider clock)
{
    public async Task<FinancialGoalProgress> ReadAsync(
        string tenant,
        string customer,
        string financialGoalId,
        CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(financialGoalId, 80))
            throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);

        var goal = await goals.ReadAsync(tenant, customer, financialGoalId, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("FINANCIAL_GOAL_NOT_FOUND", 404);
        if (goal.CustomerId != customer || goal.FinancialGoalId != financialGoalId ||
            goal.DeclarationStatus != "CUSTOMER_DEFINED" || !ValidGoal(goal.Goal))
            throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503);

        var page = await contributions.ListAsync(tenant, customer, financialGoalId, ct).ConfigureAwait(false);
        if (page.HasMore || page.Items.Count > 200)
            throw new TenantBoundaryException("GOAL_PROGRESS_INCOMPLETE", 503);

        var active = new List<ReportedGoalContribution>();
        var withdrawn = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var goalVersions = new Dictionary<int, FinancialGoalDraft>();
        foreach (var item in page.Items)
        {
            if (item.CustomerId != customer || item.Contribution.FinancialGoalId != financialGoalId ||
                !ids.Add(item.GoalContributionId) || item.Revision < 1 || item.Contribution.GoalRevision < 1 ||
                item.Contribution.Amount is <= 0 or > 999999999999.99m ||
                decimal.Round(item.Contribution.Amount, 2) != item.Contribution.Amount ||
                item.SourceGoal.Currency is not { Length: 3 } ||
                item.SourceGoal.Currency.Any(character => !char.IsAsciiLetterUpper(character)))
                throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503);
            if (!goalVersions.TryGetValue(item.Contribution.GoalRevision, out var sourceGoal))
            {
                var sourceVersion = await goals.ReadAsync(tenant, customer, financialGoalId, ct, item.Contribution.GoalRevision).ConfigureAwait(false);
                if (sourceVersion is null || sourceVersion.CustomerId != customer || sourceVersion.FinancialGoalId != financialGoalId ||
                    sourceVersion.Revision != item.Contribution.GoalRevision || sourceVersion.DeclarationStatus != "CUSTOMER_DEFINED" || !ValidGoal(sourceVersion.Goal))
                    throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503);
                sourceGoal = sourceVersion.Goal;
                goalVersions.Add(item.Contribution.GoalRevision, sourceGoal);
            }
            if (item.SourceGoal != sourceGoal)
                throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503);

            if (item.DeclarationStatus == "CUSTOMER_WITHDRAWN")
            {
                withdrawn++;
                continue;
            }
            if (item.DeclarationStatus != "CUSTOMER_REPORTED")
                throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503);
            if (!string.Equals(item.SourceGoal.Currency, goal.Goal.Currency, StringComparison.Ordinal))
                throw new TenantBoundaryException("GOAL_PROGRESS_CURRENCY_CHANGED", 409);
            active.Add(item);
        }

        decimal total;
        try { total = active.Aggregate(0m, static (sum, item) => checked(sum + item.Contribution.Amount)); }
        catch (OverflowException) { throw new TenantBoundaryException("GOAL_PROGRESS_SOURCE_INVALID", 503); }
        total = decimal.Round(total, 2, MidpointRounding.AwayFromZero);
        var remaining = decimal.Round(decimal.Max(0m, goal.Goal.TargetAmount - total), 2, MidpointRounding.AwayFromZero);
        var percent = decimal.Round(total / goal.Goal.TargetAmount * 100m, 2, MidpointRounding.AwayFromZero);
        var sources = active
            .OrderBy(item => item.Contribution.ContributionDate)
            .ThenBy(item => item.GoalContributionId, StringComparer.Ordinal)
            .Select(item => new FinancialGoalProgressSource(
                item.GoalContributionId,
                item.Revision,
                item.Contribution.GoalRevision,
                item.Contribution.Amount,
                item.Contribution.ContributionDate))
            .ToArray();

        return new FinancialGoalProgress(
            customer,
            financialGoalId,
            goal.Revision,
            goal.Goal,
            total,
            remaining,
            percent,
            sources.Length,
            withdrawn,
            active.Count == 0 ? null : active.Max(item => item.Contribution.ContributionDate),
            sources,
            clock.GetUtcNow(),
            "CUSTOMER_REPORTED",
            "NOT_DETERMINED");
    }

    private static bool ValidGoal(FinancialGoalDraft goal) =>
        !string.IsNullOrWhiteSpace(goal.Name) && goal.Name == goal.Name.Trim() && goal.Name.Length <= 120 && !goal.Name.Any(char.IsControl) &&
        goal.TargetAmount is > 0 and <= 999999999999.99m && decimal.Round(goal.TargetAmount, 2) == goal.TargetAmount &&
        goal.Currency is { Length: 3 } && goal.Currency.All(char.IsAsciiLetterUpper) && goal.TargetDate.Year is >= 1900 and <= 2199;
}
