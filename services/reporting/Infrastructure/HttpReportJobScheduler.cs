using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class HttpReportJobScheduler : IReportJobScheduler, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;
    private readonly string token;

    public HttpReportJobScheduler(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:JobManagementUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:JobManagementToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<ScheduledJob> ScheduleAsync(ReportGenerationIntent intent,
        TrustedSecurityContext security, CancellationToken cancellationToken)
    {
        var tenant = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, null, "local-uat-workspace");
        var request = new ContractRequest<ScheduleJob>(
            Vs02ContractNames.ScheduleJob,
            ContractGuard.CurrentVersion,
            "request-" + intent.JobId,
            intent.CorrelationId,
            intent.RequestId,
            security,
            intent.OperationId,
            new ScheduleJob(intent.JobId, "report-generation", ReportingIdentity.OwnerService,
                intent.OperationId, intent.CustomerId, intent.SubmittedAt));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/local/v1/jobs/report-generation"));
        message.Headers.Add("X-Monergy-Tenant", tenant);
        message.Headers.Add("X-Monergy-Job-Token", token);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("Report job identity conflict.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Report job scheduling failed.", null, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ContractResult<ScheduledJob>>(
            ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Report job response was unavailable.");
        if (result.Outcome != ContractOutcome.Success || result.Data is null ||
            result.Data.JobId != intent.JobId || result.Data.CustomerId != intent.CustomerId ||
            result.Data.JobName != "report-generation" || result.Data.PayloadReference != intent.OperationId)
            throw new HttpRequestException("Report job response was invalid.");
        return result.Data;
    }

    public void Dispose() => client.Dispose();
}
