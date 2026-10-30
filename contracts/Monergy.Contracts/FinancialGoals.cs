using System.Text.Json.Serialization;
namespace Monergy.Contracts;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FinancialGoalDraft(string Name, decimal TargetAmount, string Currency, DateOnly TargetDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFinancialGoal(string OperationId, FinancialGoalDraft Goal);
public sealed record CustomerFinancialGoal(string FinancialGoalId, string OperationId, string CustomerId,
    FinancialGoalDraft Goal, string RecordedBy, DateTimeOffset RecordedAt, string DeclarationStatus, int Revision = 1,
    string LifecycleStatus = "IN_PROGRESS", DateOnly? LifecycleAsOfDate = null, string LifecycleBasis = "CUSTOMER_REPORTED",
    string PriorityLevel = "NOT_SET", DateOnly? PriorityAsOfDate = null, string PriorityBasis = "CUSTOMER_REPORTED");
public sealed record FinancialGoalList(IReadOnlyList<CustomerFinancialGoal> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseFinancialGoal(string OperationId, int ExpectedRevision, FinancialGoalDraft Goal);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportFinancialGoalLifecycle(string OperationId, int ExpectedRevision, string Status, DateOnly AsOfDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReportFinancialGoalPriority(string OperationId, int ExpectedRevision, string Priority, DateOnly AsOfDate);
public sealed record FinancialGoalHistory(IReadOnlyList<CustomerFinancialGoal> Items, bool HasMore, int? NextBeforeRevision);
