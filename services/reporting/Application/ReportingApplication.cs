using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.Reporting.Domain;

namespace Monergy.Services.Reporting.Application;

public sealed class ReportingApplication(
    IReportSourceReader sourceReader,
    IReportRepository repository,
    IReportingAuthorizationPolicy authorizationPolicy,
    TimeProvider timeProvider)
{
    public async Task<ContractResult<TrustedFinancialReport>> GenerateAsync(
        ContractRequest<GenerateReport> request, CancellationToken cancellationToken = default)
    {
        var error = Validate(request, ReportingContractNames.GenerateReport, request.Payload.CustomerId);
        if (error is not null) return Rejected<GenerateReport>(request, error);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ContractResult<TrustedFinancialReport>.Rejected(request, "report.idempotency.required",
                ContractErrorCategory.ValidationError, "A caller-supplied idempotency key is required.");
        }

        var accessError = await authorizationPolicy.AuthorizeAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null) return Rejected<GenerateReport>(request, accessError);

        var identity = new ReportOperationIdentity(request.ContractName, request.ContractVersion,
            request.Payload.CustomerId, request.IdempotencyKey);
        var operation = await repository.GetOrCreateAsync(identity, async operationCancellationToken =>
        {
            var snapshot = await sourceReader.ReadAsync(request.Payload.CustomerId, operationCancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return ReportGenerationAttempt.Rejected(new("report.source.not-found",
                    ContractErrorCategory.NotFound, "No authorized report source is available.", false,
                    request.CorrelationId));
            }
            if (!string.Equals(snapshot.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal))
            {
                return ReportGenerationAttempt.Rejected(new("report.source.boundary-invalid",
                    ContractErrorCategory.AccessDenied, "The report source is outside the current customer boundary.",
                    false, request.CorrelationId));
            }

            return ReportGenerationAttempt.Succeeded(BuildReport(snapshot, identity, timeProvider.GetUtcNow()));
        }, report => new("CID-053", $"report-generated-{report.ReportId}",
            ReportingContractNames.ReportGenerated, ContractGuard.CurrentVersion, report.GeneratedAt,
            request.CorrelationId, request.CausationId, ReportingAuthority.ReportingService, "Report",
            report.ReportId, new(report.ReportId, report.CustomerId, report.State,
                report.SourceFinancialReferences, report.EvidenceReferences,
                report.FinancialProvenanceReferences, report.CalculationLineageReferences,
                report.AiResponseTraceReference, report.AuditCompatibilityReferenceId,
                report.Export.Sha256)), cancellationToken).ConfigureAwait(false);

        if (operation.Error is not null) return Rejected<GenerateReport>(request, operation.Error);

        var report = operation.Report!;
        return ContractResult<TrustedFinancialReport>.Succeeded(request, report);
    }

    public async Task<ContractResult<TrustedFinancialReport>> GetAsync(
        ContractRequest<GetReport> request, CancellationToken cancellationToken = default)
    {
        var error = Validate(request, ReportingContractNames.GetReport, request.Payload.CustomerId);
        if (error is not null || string.IsNullOrWhiteSpace(request.Payload.ReportId))
        {
            return Rejected<GetReport>(request, error ?? new("report.identity.invalid",
                ContractErrorCategory.ValidationError, "A report identifier is required.", false, request.CorrelationId));
        }
        var accessError = await authorizationPolicy.AuthorizeAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (accessError is not null) return Rejected<GetReport>(request, accessError);
        var report = repository.Find(request.Payload.CustomerId, request.Payload.ReportId);
        return report is null
            ? ContractResult<TrustedFinancialReport>.Rejected(request, "report.not-found",
                ContractErrorCategory.NotFound, "The report is unavailable in the current customer boundary.")
            : ContractResult<TrustedFinancialReport>.Succeeded(request, report);
    }

    private static ContractError? Validate<T>(ContractRequest<T> request, string name, string customerId)
    {
        var contractError = ContractGuard.Validate(request, name);
        if (contractError is not null) return contractError;
        return string.IsNullOrWhiteSpace(customerId) || customerId != request.Security.Access.CustomerId
            ? new("report.customer.invalid", ContractErrorCategory.AccessDenied,
                "The report request must match the current customer boundary.", false, request.CorrelationId)
            : null;
    }

    private static TrustedFinancialReport BuildReport(ReportSourceSnapshot snapshot,
        ReportOperationIdentity identity, DateTimeOffset generatedAt)
    {
        var items = snapshot.Items.OrderBy(item => item.Label, StringComparer.Ordinal).ToImmutableArray();
        var identityMaterial = string.Join("|", identity.ContractName, identity.ContractVersion,
            identity.CustomerId, identity.IdempotencyKey);
        var reportId = $"report-{Hash(identityMaterial)[..20].ToLowerInvariant()}";
        var exportModel = new
        {
            reportId,
            customerId = snapshot.CustomerId,
            state = ReportingAuthority.Generated,
            generatedAt,
            items = items.Select(item => new
            {
                item.Label,
                item.Value,
                item.Unit,
                source = item.Source,
            }),
        };
        var content = JsonSerializer.Serialize(exportModel, ContractJson.Options);
        var export = new ReportExport($"trusted-financial-report-{reportId}.json", "application/json",
            content, Hash(content));
        return new(reportId, snapshot.CustomerId, ReportingAuthority.Generated, generatedAt, items,
            Distinct(items.Select(item => item.Source.SourceId)),
            Distinct(items.Select(item => item.Source.EvidenceReferenceId)),
            Distinct(items.Select(item => item.Source.FinancialProvenanceReferenceId)),
            Distinct(items.Select(item => item.Source.CalculationLineageReferenceId)),
            snapshot.AiResponseTraceReference, $"audit-compatible-{reportId}", export);
    }

    private static ImmutableArray<string> Distinct(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ContractResult<TrustedFinancialReport> Rejected<T>(ContractRequest<T> request, ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId,
            ContractOutcome.Rejected, null, error);
}
