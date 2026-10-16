using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductReportGenerationRequest(string OperationId);

public static class ProductReportingEndpoints
{
    public static void AddProductReporting(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        builder.Services.AddSingleton<ProductReportingClient>();
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
                new GenerateReport(customer.CustomerId));

            var result = await reporting.GenerateAsync(
                session.TenantId,
                session.AuthenticationContextId,
                request,
                http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(ReadResult(result, customer.CustomerId));
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
        if (result.Data.CustomerId != customerId)
            throw new TenantBoundaryException("REPORTING_OWNER_RESPONSE_INVALID", 503);
        return result.Data;
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

    public Task<ContractResult<TrustedFinancialReport>> GenerateAsync(
        string tenantId,
        string sessionId,
        ContractRequest<GenerateReport> request,
        CancellationToken cancellationToken) =>
        SendAsync<GenerateReport, TrustedFinancialReport>(
            tenantId, sessionId, "contracts/cid-051/v1", request, cancellationToken);

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
