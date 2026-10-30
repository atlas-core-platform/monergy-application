using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductReportGenerationRequest(
    string OperationId,
    FinancialReviewReceiptSelection? FinancialReview = null);

public sealed record ProductReportCancellationRequest(string OperationId);

public sealed record ProductReportRetryRequest(string OperationId);

public static class ProductReportingEndpoints
{
    private static readonly string[] FinancialReviewRules =
    [
        "monergy.asset-allocation",
        "monergy.customer-reviewed-monthly-debt-service-coverage",
        "monergy.customer-reviewed-monthly-emi-share",
        "monergy.monthly-net-cashflow-rate",
    ];

    public static void AddProductReporting(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        builder.Services.AddSingleton<ProductReportingClient>();
        builder.Services.AddSingleton<ProductReportJobClient>();
    }

    public static void MapProductReporting(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;

        app.MapPost("/product-api/v1/reports", async (
            ProductReportGenerationRequest submission,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductReportingClient reporting) =>
        {
            ValidateOperation(submission.OperationId);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "reporting.report.generate", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

            var request = new ContractRequest<GenerateReport>(
                ReportingContractNames.GenerateReport,
                ContractGuard.CurrentVersion,
                "request-product-report-" + submission.OperationId,
                "correlation-product-report-" + submission.OperationId,
                null,
                Security(session, customer.CustomerId, decision),
                submission.OperationId,
                new GenerateReport(customer.CustomerId, submission.FinancialReview));

            var result = await reporting.ScheduleAsync(
                session.TenantId,
                session.AuthenticationContextId,
                request,
                http.RequestAborted).ConfigureAwait(false);
            var job = ReadJobResult(result, customer.CustomerId, submission.OperationId);
            return Results.Accepted($"/product-api/v1/reports/jobs/{job.JobId}", job);
        });

        app.MapGet("/product-api/v1/reports/jobs/{jobId}", async (
            string jobId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductReportJobClient jobs) =>
        {
            if (!TenantAccessClient.Identifier(jobId, 160) ||
                !jobId.StartsWith("job-report-", StringComparison.Ordinal))
                throw new TenantBoundaryException("REPORT_JOB_ID_INVALID", 400);
            var operationId = jobId["job-report-".Length..];
            ValidateOperation(operationId);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "reporting.report.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var request = new ContractRequest<GetJobStatus>(
                Vs02ContractNames.GetJobStatus,
                ContractGuard.CurrentVersion,
                "request-product-report-job-" + operationId,
                "correlation-product-report-job-" + operationId,
                null,
                Security(session, customer.CustomerId, decision),
                null,
                new GetJobStatus(jobId, customer.CustomerId));
            var scheduled = await jobs.StatusAsync(
                session.TenantId, request, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ReadJob(scheduled, customer.CustomerId, operationId));
        });

        app.MapPost("/product-api/v1/reports/jobs/{jobId}/cancellations", async (
            string jobId,
            ProductReportCancellationRequest cancellation,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductReportJobClient jobs) =>
        {
            if (!TenantAccessClient.Identifier(jobId, 160) ||
                !jobId.StartsWith("job-report-", StringComparison.Ordinal))
                throw new TenantBoundaryException("REPORT_JOB_ID_INVALID", 400);
            var reportOperationId = jobId["job-report-".Length..];
            ValidateOperation(reportOperationId);
            ValidateOperation(cancellation.OperationId);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "reporting.report.generate", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var request = new ContractRequest<CancelJob>(
                Vs02ContractNames.CancelJob,
                ContractGuard.CurrentVersion,
                "request-product-report-cancel-" + cancellation.OperationId,
                "correlation-product-report-cancel-" + cancellation.OperationId,
                null,
                Security(session, customer.CustomerId, decision),
                "report-cancel-" + cancellation.OperationId,
                new CancelJob(jobId, customer.CustomerId, "customer-requested"));
            var scheduled = await jobs.CancelAsync(
                session.TenantId, request, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ReadJob(scheduled, customer.CustomerId, reportOperationId));
        });

        app.MapPost("/product-api/v1/reports/jobs/{jobId}/retries", async (
            string jobId,
            ProductReportRetryRequest retry,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductReportJobClient jobs) =>
        {
            if (!TenantAccessClient.Identifier(jobId, 160) ||
                !jobId.StartsWith("job-report-", StringComparison.Ordinal))
                throw new TenantBoundaryException("REPORT_JOB_ID_INVALID", 400);
            var reportOperationId = jobId["job-report-".Length..];
            ValidateOperation(reportOperationId);
            ValidateOperation(retry.OperationId);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "reporting.report.generate", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var request = new ContractRequest<GetJobStatus>(
                Vs02ContractNames.GetJobStatus,
                ContractGuard.CurrentVersion,
                "request-product-report-retry-" + retry.OperationId,
                "correlation-product-report-retry-" + retry.OperationId,
                null,
                Security(session, customer.CustomerId, decision),
                "report-retry-" + retry.OperationId,
                new GetJobStatus(jobId, customer.CustomerId));
            var scheduled = await jobs.RetryAsync(
                session.TenantId, request, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ReadJob(scheduled, customer.CustomerId, reportOperationId));
        });

        app.MapGet("/product-api/v1/reports/{reportId}", async (
            string reportId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductReportingClient reporting) =>
        {
            if (!TenantAccessClient.Identifier(reportId, 160) ||
                !reportId.StartsWith("report-", StringComparison.Ordinal))
                throw new TenantBoundaryException("REPORT_ID_INVALID", 400);

            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "reporting.report.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

            var requestId = "request-product-report-read-" + Guid.NewGuid().ToString("N");
            var request = new ContractRequest<GetReport>(
                ReportingContractNames.GetReport,
                ContractGuard.CurrentVersion,
                requestId,
                "correlation-product-report-read-" + Guid.NewGuid().ToString("N"),
                null,
                Security(session, customer.CustomerId, decision),
                null,
                new GetReport(customer.CustomerId, reportId));

            var result = await reporting.GetAsync(
                session.TenantId,
                session.AuthenticationContextId,
                request,
                http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ReadResult(result, customer.CustomerId));
        });
    }

    private static TrustedFinancialReport ReadResult(
        ContractResult<TrustedFinancialReport> result,
        string customerId)
    {
        if (result.Outcome == ContractOutcome.Rejected &&
            result.Error?.Category == ContractErrorCategory.NotFound)
            throw new TenantBoundaryException("REPORT_NOT_FOUND", 404);
        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw new TenantBoundaryException(result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired
                    => "ACCESS_DENIED",
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable
                    => "REPORTING_OWNER_UNAVAILABLE",
                _ => "REPORT_GENERATION_REJECTED",
            }, result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => 503,
                _ => 400,
            });
        var report = result.Data;
        var calculations = report.Items.Where(item => item.Source.SourceType == "FinancialCalculation").ToArray();
        var facts = report.Items.Where(item => item.Source.SourceType == "FinancialFact").ToArray();
        var taxRecords = report.Items.Where(item => item.Source.SourceType == "TaxRecord").ToArray();
        if (report.CustomerId != customerId ||
            report.State != "GENERATED" ||
            !TenantAccessClient.Identifier(report.ReportId, 160) ||
            !report.ReportId.StartsWith("report-", StringComparison.Ordinal) ||
            report.Items.Length is < 1 or > 100 ||
            report.Items.Length != facts.Length + calculations.Length + taxRecords.Length ||
            facts.Any(item =>
                item.Source.SourceType != "FinancialFact" ||
                item.Source.AuthoritativeOwner != "Financial Profile Service" ||
                string.IsNullOrWhiteSpace(item.Source.SourceId) ||
                string.IsNullOrWhiteSpace(item.Source.EvidenceReferenceId) ||
                string.IsNullOrWhiteSpace(item.Source.FinancialProvenanceReferenceId) ||
                item.Source.CalculationLineageReferenceId is not null ||
                item.Source.RuleId is not null || item.Source.RuleVersion is not null ||
                item.Source.DefinitionHash is not null || item.Source.NumericSemantics is not null ||
                item.Source.SourceCalculationIds is not null ||
                item.Source.FinancialFactReferenceId is not null ||
                item.Source.FinancialFactRevision is not null ||
                item.Source.DocumentVersionReferenceId is not null) ||
            taxRecords.Any(item => !ValidTaxRecord(item)) ||
            calculations.Any(item => !ValidCalculation(item)) ||
            calculations.Length > 0 &&
                (!calculations.Select(item => item.Source.RuleId).Distinct(StringComparer.Ordinal).Order()
                    .SequenceEqual(FinancialReviewRules, StringComparer.Ordinal) ||
                 report.CalculationLineageReferences.Length != 4) ||
            calculations.Length == 0 && report.CalculationLineageReferences.Length != 0 ||
            report.AiResponseTraceReference is not null ||
            report.SourceFinancialReferences.Length !=
                report.Items.Select(item => item.Source.SourceId).Distinct(StringComparer.Ordinal).Count() ||
            report.Export.MediaType != "application/json" ||
            report.Export.FileName != $"trusted-financial-report-{report.ReportId}.json" ||
            report.Export.Sha256.Length != 64 ||
            !report.Export.Sha256.All(char.IsAsciiHexDigit))
            throw new TenantBoundaryException("REPORTING_OWNER_RESPONSE_INVALID", 503);
        return report;
    }

    private static bool ValidTaxRecord(ReportLineItem item)
    {
        var source = item.Source;
        return source.AuthoritativeOwner == "Financial Profile Service" &&
            TenantAccessClient.Identifier(source.SourceId, 80) &&
            TenantAccessClient.Identifier(source.EvidenceReferenceId, 160) &&
            TenantAccessClient.Identifier(source.FinancialProvenanceReferenceId, 160) &&
            source.CalculationLineageReferenceId is null &&
            source.RuleId is null && source.RuleVersion is null && source.DefinitionHash is null &&
            source.NumericSemantics is null && source.SourceCalculationIds is null &&
            TenantAccessClient.Identifier(source.FinancialFactReferenceId, 160) &&
            source.FinancialFactRevision is > 0 &&
            TenantAccessClient.Identifier(source.DocumentVersionReferenceId, 160) &&
            item.Unit.Length == 3 && item.Unit.All(char.IsAsciiLetterUpper);
    }

    private static ReportGenerationJob ReadJobResult(
        ContractResult<ReportGenerationJob> result,
        string customerId,
        string operationId)
    {
        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw new TenantBoundaryException(result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => "ACCESS_DENIED",
                ContractErrorCategory.Conflict => "REPORT_OPERATION_CONFLICT",
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable =>
                    "REPORT_JOB_OWNER_UNAVAILABLE",
                _ => "REPORT_GENERATION_REJECTED",
            }, result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
                ContractErrorCategory.Conflict => 409,
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => 503,
                _ => 400,
            });
        return ValidateJob(result.Data, customerId, operationId);
    }

    private static ReportGenerationJob ReadJob(ScheduledJob job, string customerId, string operationId) =>
        ValidateJob(new(job.JobId,
            ReportingIdentity.ReportId(ContractGuard.CurrentVersion, customerId, operationId),
            job.State, job.Attempt, job.FailureCode, job.Retryable, job.UpdatedAt),
            customerId, operationId);

    private static ReportGenerationJob ValidateJob(
        ReportGenerationJob job,
        string customerId,
        string operationId)
    {
        if (job.JobId != ReportingIdentity.JobId(operationId) ||
            job.ReportId != ReportingIdentity.ReportId(ContractGuard.CurrentVersion, customerId, operationId) ||
            job.Attempt < 0 ||
            job.FailureCode?.Length > 160)
            throw new TenantBoundaryException("REPORT_JOB_OWNER_RESPONSE_INVALID", 503);
        return job;
    }

    private static bool ValidCalculation(ReportLineItem item)
    {
        var source = item.Source;
        if (source.AuthoritativeOwner != "Financial Rules Service" ||
            string.IsNullOrWhiteSpace(source.SourceId) ||
            source.CalculationLineageReferenceId != source.SourceId ||
            source.EvidenceReferenceId is not null || source.FinancialProvenanceReferenceId is not null ||
            source.FinancialFactReferenceId is not null || source.FinancialFactRevision is not null ||
            source.DocumentVersionReferenceId is not null ||
            source.SourceCalculationIds is null || source.SourceCalculationIds.Count == 0 ||
            source.SourceCalculationIds.Any(value => !TenantAccessClient.Identifier(value, 160)))
            return false;
        return source.RuleId switch
        {
            AssetAllocationDefinition.Id => item.Unit == "%" &&
                source.RuleVersion == AssetAllocationDefinition.Version &&
                source.DefinitionHash == AssetAllocationDefinition.DefinitionHash &&
                source.NumericSemantics == AssetAllocationDefinition.Semantics,
            MonthlyNetCashflowRateDefinition.Id => item.Unit == "%" &&
                source.RuleVersion == MonthlyNetCashflowRateDefinition.Version &&
                source.DefinitionHash == MonthlyNetCashflowRateDefinition.DefinitionHash &&
                source.NumericSemantics == MonthlyNetCashflowRateDefinition.Semantics,
            MonthlyEmiShareDefinition.Id => item.Unit == "%" &&
                source.RuleVersion == MonthlyEmiShareDefinition.Version &&
                source.DefinitionHash == MonthlyEmiShareDefinition.DefinitionHash &&
                source.NumericSemantics == MonthlyEmiShareDefinition.Semantics,
            MonthlyDebtServiceCoverageDefinition.Id => item.Unit == "x" &&
                source.RuleVersion == MonthlyDebtServiceCoverageDefinition.Version &&
                source.DefinitionHash == MonthlyDebtServiceCoverageDefinition.DefinitionHash &&
                source.NumericSemantics == MonthlyDebtServiceCoverageDefinition.Semantics,
            _ => false,
        };
    }

    private static TrustedSecurityContext Security(
        CustomerOwnerSession session,
        string customerId,
        CustomerAccessDecision decision) => new(
        new ActorContext(session.ActorId, "Customer", DateTimeOffset.UtcNow, session.AuthenticationContextId),
        new WorkloadContext("customer-web", "local-uat-workspace:" + session.TenantId),
        new AccessContext("customer-advice", null, decision.DecisionId, customerId));

    private static void ValidateOperation(string operationId)
    {
        if (operationId.Length != 32 || operationId.Any(character =>
            !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new TenantBoundaryException("REPORT_OPERATION_INVALID", 400);
    }
}

public sealed class ProductReportingClient : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;

    public ProductReportingClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:ReportingUrl"));
    }

    public Task<ContractResult<ReportGenerationJob>> ScheduleAsync(
        string tenantId,
        string sessionId,
        ContractRequest<GenerateReport> request,
        CancellationToken cancellationToken) =>
        SendAsync<GenerateReport, ReportGenerationJob>(
            tenantId, sessionId, "contracts/cid-051/v2", request, cancellationToken);

    public Task<ContractResult<TrustedFinancialReport>> GetAsync(
        string tenantId,
        string sessionId,
        ContractRequest<GetReport> request,
        CancellationToken cancellationToken) =>
        SendAsync<GetReport, TrustedFinancialReport>(
            tenantId, sessionId, "contracts/cid-052/v1", request, cancellationToken);

    private async Task<ContractResult<TData>> SendAsync<TPayload, TData>(
        string tenantId,
        string sessionId,
        string path,
        ContractRequest<TPayload> request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.Add("X-Monergy-Tenant", tenantId);
        message.Headers.Add("X-Monergy-Session", sessionId);

        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TenantBoundaryException("TENANT_SESSION_INVALID", 401);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
                throw new TenantBoundaryException("REPORTING_OWNER_UNAVAILABLE", 503);

            var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("REPORTING_OWNER_RESPONSE_INVALID", 503);
            if (result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId)
                throw new TenantBoundaryException("REPORTING_OWNER_RESPONSE_INVALID", 503);
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new TenantBoundaryException("REPORTING_OWNER_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TenantBoundaryException("REPORTING_OWNER_UNAVAILABLE", 503);
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed class ProductReportJobClient : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;
    private readonly string token;

    public ProductReportJobClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:JobManagementUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:Product:JobManagementToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<ScheduledJob> StatusAsync(
        string tenantId,
        ContractRequest<GetJobStatus> request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/local/v1/jobs/status"));
        message.Headers.Add("X-Monergy-Tenant", tenantId);
        message.Headers.Add("X-Monergy-Job-Token", token);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new TenantBoundaryException("REPORT_JOB_NOT_FOUND", 404);
            if (!response.IsSuccessStatusCode)
                throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<ScheduledJob>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("REPORT_JOB_OWNER_RESPONSE_INVALID", 503);
            if (result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId ||
                result.Outcome != ContractOutcome.Success || result.Data is null ||
                result.Data.JobId != request.Payload.JobId ||
                result.Data.CustomerId != request.Payload.CustomerId ||
                result.Data.JobName != "report-generation" ||
                result.Data.OwnerService != ReportingIdentity.OwnerService)
                throw new TenantBoundaryException("REPORT_JOB_OWNER_RESPONSE_INVALID", 503);
            return result.Data;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
        }
    }

    public Task<ScheduledJob> CancelAsync(
        string tenantId,
        ContractRequest<CancelJob> request,
        CancellationToken cancellationToken) =>
        SendAsync(tenantId, "internal/local/v1/jobs/report-generation/cancel", request,
            request.Payload.JobId, request.Payload.CustomerId, cancellationToken);

    public Task<ScheduledJob> RetryAsync(
        string tenantId,
        ContractRequest<GetJobStatus> request,
        CancellationToken cancellationToken) =>
        SendAsync(tenantId, "internal/local/v1/jobs/report-generation/retry", request,
            request.Payload.JobId, request.Payload.CustomerId, cancellationToken);

    private async Task<ScheduledJob> SendAsync<TPayload>(
        string tenantId,
        string path,
        ContractRequest<TPayload> request,
        string jobId,
        string customerId,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path));
        message.Headers.Add("X-Monergy-Tenant", tenantId);
        message.Headers.Add("X-Monergy-Job-Token", token);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new TenantBoundaryException("REPORT_JOB_NOT_FOUND", 404);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new TenantBoundaryException("REPORT_JOB_STATE_CONFLICT", 409);
            if (!response.IsSuccessStatusCode)
                throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<ScheduledJob>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("REPORT_JOB_OWNER_RESPONSE_INVALID", 503);
            if (result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId ||
                result.Outcome != ContractOutcome.Success || result.Data is null ||
                result.Data.JobId != jobId ||
                result.Data.CustomerId != customerId ||
                result.Data.JobName != "report-generation" ||
                result.Data.OwnerService != ReportingIdentity.OwnerService)
                throw new TenantBoundaryException("REPORT_JOB_OWNER_RESPONSE_INVALID", 503);
            return result.Data;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TenantBoundaryException("REPORT_JOB_OWNER_UNAVAILABLE", 503);
        }
    }

    public void Dispose() => client.Dispose();
}
