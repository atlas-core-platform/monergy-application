using System.Text.Json.Serialization;
namespace Monergy.Contracts;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GoalContributionDraft(string FinancialGoalId,int GoalRevision,decimal Amount,DateOnly ContributionDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGoalContribution(string OperationId,GoalContributionDraft Contribution);
public sealed record ReportedGoalContribution(string GoalContributionId,string OperationId,string CustomerId,
    GoalContributionDraft Contribution,FinancialGoalDraft SourceGoal,string RecordedBy,DateTimeOffset RecordedAt,string DeclarationStatus,int Revision=1);
public sealed record GoalContributionList(IReadOnlyList<ReportedGoalContribution> Items,bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseGoalContribution(string OperationId,int ExpectedRevision,GoalContributionDraft Contribution);
public sealed record GoalContributionHistory(IReadOnlyList<ReportedGoalContribution> Items,bool HasMore,int? NextBeforeRevision);
