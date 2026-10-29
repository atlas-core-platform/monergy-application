using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;

namespace Monergy.Services.Reporting.Application;

public sealed class ReportGenerationJobs(
    IReportGenerationIntentRepository intents,
    IReportJobScheduler scheduler,
    ReportingApplication reporting,
    IReportingAuthorizationPolicy authorization,
    TimeProvider timeProvider)
{
    public async Task<ContractResult<ReportGenerationJob>> ScheduleAsync(
        ContractRequest<GenerateReport> request,
        CancellationToken cancellationToken = default)
    {
        var error = Validate(request);
        if (error is not null) return Rejected(request, error);
        var denied = await authorization.AuthorizeAsync(request.Security, request.Payload.CustomerId,
            request.ContractName, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        if (denied is not null) return Rejected(request, denied);

        var operationId = request.IdempotencyKey!;
        var intent = new ReportGenerationIntent(
            operationId,
            ReportingIdentity.JobId(operationId),
            ReportingIdentity.ReportId(request.ContractVersion, request.Payload.CustomerId, operationId),
            request.Payload.CustomerId,
            request.Security.Actor.ActorId,
            request.ContractVersion,
            request.RequestId,
            request.CorrelationId,
            request.CausationId,
            request.Payload.FinancialReview,
            Fingerprint(request.Payload),
            timeProvider.GetUtcNow());
        try
        {
            var persisted = await intents.GetOrCreateAsync(intent, cancellationToken).ConfigureAwait(false);
            var job = await scheduler.ScheduleAsync(persisted.Intent, request.Security, cancellationToken)
                .ConfigureAwait(false);
            return ContractResult<ReportGenerationJob>.Succeeded(request,
                View(job, persisted.Intent.ReportId));
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ReportGenerationJob>.Rejected(request,
                "report.job.conflict", ContractErrorCategory.Conflict,
                "The report operation is already bound to different source selections.");
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or NotSupportedException)
        {
            return ContractResult<ReportGenerationJob>.Failed(request,
                "report.job.unavailable", ContractErrorCategory.DependencyFailure,
                "The background report service is unavailable.", true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ContractResult<ReportGenerationJob>.Failed(request,
                "report.job.timeout", ContractErrorCategory.DependencyFailure,
                "The background report service did not confirm scheduling in time.", true);
        }
    }

    public async Task<ContractResult<TrustedFinancialReport>> ExecuteAsync(
        string operationId,
        string jobId,
        TrustedSecurityContext security,
        CancellationToken cancellationToken = default)
    {
        var intent = await intents.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.JobId != jobId || intent.CustomerId != security.Access.CustomerId ||
            intent.ActorId != security.Actor.ActorId)
        {
            return new(ReportingContractNames.GenerateReport, ContractGuard.CurrentVersion,
                "request-report-job-missing", "correlation-report-job-missing", ContractOutcome.Rejected,
                null, new("report.job.intent-not-found", ContractErrorCategory.NotFound,
                    "The authorized report request was not found.", false, "correlation-report-job-missing"));
        }
        var request = new ContractRequest<GenerateReport>(
            ReportingContractNames.GenerateReport,
            intent.ContractVersion,
            intent.RequestId,
            intent.CorrelationId,
            intent.CausationId,
            security,
            intent.OperationId,
            new GenerateReport(intent.CustomerId, intent.FinancialReview));
        return await reporting.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public static ReportGenerationJob View(ScheduledJob job, string reportId) =>
        new(job.JobId, reportId, job.State, job.Attempt, job.FailureCode, job.Retryable, job.UpdatedAt);

    private static ContractError? Validate(ContractRequest<GenerateReport> request)
    {
        var contract = ContractGuard.Validate(request, ReportingContractNames.GenerateReport);
        if (contract is not null) return contract;
        if (request.Payload.CustomerId != request.Security.Access.CustomerId)
            return new("report.customer.invalid", ContractErrorCategory.AccessDenied,
                "The report request must match the current customer boundary.", false, request.CorrelationId);
        if (request.IdempotencyKey is not { Length: 32 } operationId ||
            operationId.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            return new("report.operation.invalid", ContractErrorCategory.ValidationError,
                "A canonical report operation identifier is required.", false, request.CorrelationId);
        if (request.Payload.FinancialReview is { } review && !ValidReview(review))
            return new("report.financial-review.invalid", ContractErrorCategory.ValidationError,
                "Four canonical Financial Rules receipt identifiers are required.", false, request.CorrelationId);
        return null;
    }

    private static bool ValidReview(FinancialReviewReceiptSelection value)
    {
        var identifiers = new[]
        {
            value.AssetAllocationCalculationId,
            value.MonthlyNetCashflowRateCalculationId,
            value.MonthlyEmiShareCalculationId,
            value.MonthlyDebtServiceCoverageCalculationId,
        };
        return identifiers.All(identifier => !string.IsNullOrWhiteSpace(identifier) &&
            identifier.Length <= 80 && identifier.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) &&
            identifiers.Distinct(StringComparer.Ordinal).Count() == identifiers.Length;
    }

    private static string Fingerprint(GenerateReport payload) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ContractJson.Options)))).ToLowerInvariant();

    private static ContractResult<ReportGenerationJob> Rejected(
        ContractRequest<GenerateReport> request,
        ContractError error) => new(request.ContractName, request.ContractVersion, request.RequestId,
        request.CorrelationId, ContractOutcome.Rejected, null, error);
}
