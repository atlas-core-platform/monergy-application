namespace Monergy.Contracts;

public sealed record FinancialGoalProgressSource(
    string GoalContributionId,
    int ContributionRevision,
    int GoalRevision,
    decimal Amount,
    DateOnly ContributionDate);

public sealed record FinancialGoalProgress(
    string CustomerId,
    string FinancialGoalId,
    int GoalRevision,
    FinancialGoalDraft Goal,
    decimal ReportedContributionAmount,
    decimal RemainingAmount,
    decimal ProgressPercent,
    int IncludedContributionCount,
    int WithdrawnContributionCount,
    DateOnly? LatestContributionDate,
    IReadOnlyList<FinancialGoalProgressSource> Sources,
    DateTimeOffset ObservedAt,
    string ProgressBasis,
    string AchievementStatus);
