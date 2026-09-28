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
    void Save(TrustedFinancialReport report);
    TrustedFinancialReport? Find(string customerId, string reportId);
}

public interface IReportEvidenceSink
{
    void Record(DomainEvent<ReportGeneratedPayload> generatedEvent);
}
