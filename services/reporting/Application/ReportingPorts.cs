using Monergy.Contracts;
using Monergy.Services.Reporting.Domain;

namespace Monergy.Services.Reporting.Application;

public interface IReportingAuthorizationPolicy
{
    Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId,
        string operation, string correlationId, CancellationToken cancellationToken);
}

public interface IReportSourceReader
{
    Task<ReportSourceSnapshot?> ReadAsync(string customerId, CancellationToken cancellationToken);
}

public interface IReportRepository
{
    Task<ReportOperationResult> GetOrCreateAsync(
        ReportOperationIdentity identity,
        Func<CancellationToken, Task<ReportGenerationAttempt>> reportFactory,
        CancellationToken cancellationToken = default);

    TrustedFinancialReport? Find(string customerId, string reportId);
}

public sealed record ReportOperationIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey);

public sealed record ReportGenerationAttempt(TrustedFinancialReport? Report, ContractError? Error)
{
    public static ReportGenerationAttempt Succeeded(TrustedFinancialReport report) => new(report, null);
    public static ReportGenerationAttempt Rejected(ContractError error) => new(null, error);
}

public sealed record ReportOperationResult(TrustedFinancialReport? Report, ContractError? Error, bool Created);

public interface IReportEvidenceSink
{
    void Record(DomainEvent<ReportGeneratedPayload> generatedEvent);
}
