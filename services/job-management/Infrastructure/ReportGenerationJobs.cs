using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed class HttpReportGenerationTarget : IJobExecutionTarget, IDisposable
{
    private sealed record ExecuteReportGenerationJob(
        string OperationId,
        string JobId,
        string CustomerId,
        TrustedSecurityContext Security);

    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;
    private readonly string token;

    public HttpReportGenerationTarget(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:ReportingUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:ReportingToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<JobExecutionResult> ExecuteAsync(DurableJobRecord job,
        CancellationToken cancellationToken)
    {
        if (job.JobName != "report-generation" ||
            job.OwnerService != ReportingIdentity.OwnerService ||
            job.JobId != ReportingIdentity.JobId(job.PayloadReference) ||
            job.PayloadReference.Length != 32)
            return JobExecutionResult.Failed("job.report-generation.intent-invalid", false);
        var tenant = LocalTenantSecurityContext.RequireTenant(
            job.Security.Workload?.WorkloadIdentityId, null, "local-uat-workspace");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/local/v1/report-generation/execute"));
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Report-Token", token);
        request.Content = JsonContent.Create(new ExecuteReportGenerationJob(
            job.PayloadReference, job.JobId, job.CustomerId, job.Security), options: ContractJson.Options);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return JobExecutionResult.Failed("job.report-generation.authorization-denied", false);
            if (!response.IsSuccessStatusCode)
                return JobExecutionResult.Failed("job.report-generation.owner-unavailable",
                    (int)response.StatusCode >= 500);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<TrustedFinancialReport>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false);
            if (result?.Outcome != ContractOutcome.Success || result.Data is null ||
                result.Data.CustomerId != job.CustomerId ||
                result.Data.ReportId != ReportingIdentity.ReportId(
                    job.JobVersion, job.CustomerId, job.PayloadReference))
                return JobExecutionResult.Failed("job.report-generation.response-invalid", false);
            return JobExecutionResult.Completed(result.Data.ReportId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return JobExecutionResult.Failed("job.report-generation.timeout", true); }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { return JobExecutionResult.Failed("job.report-generation.owner-unavailable", true); }
    }

    public void Dispose() => client.Dispose();
}
