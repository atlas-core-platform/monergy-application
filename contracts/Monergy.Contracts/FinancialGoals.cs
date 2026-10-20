using System.Text.Json.Serialization;
namespace Monergy.Contracts;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FinancialGoalDraft(string Name, decimal TargetAmount, string Currency, DateOnly TargetDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFinancialGoal(string OperationId, FinancialGoalDraft Goal);
public sealed record CustomerFinancialGoal(string FinancialGoalId, string OperationId, string CustomerId,
    FinancialGoalDraft Goal, string RecordedBy, DateTimeOffset RecordedAt, string DeclarationStatus, int Revision = 1);
public sealed record FinancialGoalList(IReadOnlyList<CustomerFinancialGoal> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseFinancialGoal(string OperationId, int ExpectedRevision, FinancialGoalDraft Goal);
public sealed record FinancialGoalHistory(IReadOnlyList<CustomerFinancialGoal> Items, bool HasMore, int? NextBeforeRevision);
