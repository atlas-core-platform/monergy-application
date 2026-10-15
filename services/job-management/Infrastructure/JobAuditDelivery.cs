using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public static class JobAuditDeliveryOptions
{
    public static bool Selected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:AuditDelivery:Enabled");

    public static Uri Endpoint(IConfiguration configuration)
    {
        var value = TenantAccessIntegration.Required(configuration, "Monergy:AuditDelivery:Url");
        return TenantAccessIntegration.LoopbackRoot(value);
    }
}

public sealed class HttpJobAuditEventConsumer(
    HttpClient client,
    IConfiguration configuration,
    TenantBoundaryOptions tenants,
    IJobRepository jobs) : IGovernedEventConsumer<AuditableEvent>
{
    private const string Recipient = "Audit Service LOCAL/UAT composition";
    private readonly string token = RequiredToken(configuration);

    public async Task<bool> ConsumeAsync(AuditableEvent source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var job = await jobs.GetAsync(source.SubjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The audit source job is unavailable.");
        if (!tenants.CustomerTenants.TryGetValue(job.CustomerId, out var tenantId))
            throw new InvalidOperationException("The audit source job has no governed tenant binding.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "reference/events/job-audit")
        {
            Content = JsonContent.Create(source, options: ContractJson.Options),
        };
        request.Headers.Add("X-Monergy-Reference-Transport", token);
        request.Headers.Add("X-Monergy-Reference-Tenant", tenantId);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The Audit owner rejected Job Management event delivery with HTTP {(int)response.StatusCode}.");

        JobAuditReceipt receipt;
        try
        {
            receipt = await response.Content.ReadFromJsonAsync<JobAuditReceipt>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException("The Audit owner returned an empty Job Management receipt.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("The Audit owner returned a malformed Job Management receipt.", exception);
        }
        if (receipt.Recipient != Recipient ||
            receipt.TenantId != tenantId ||
            receipt.SourceEventId != source.EventId ||
            receipt.SourceContractId != source.ContractId ||
            receipt.EventName != source.EventName ||
            receipt.EventVersion != source.EventVersion ||
            receipt.Producer != source.Producer ||
            receipt.SubjectType != source.SubjectType ||
            receipt.SubjectId != source.SubjectId ||
            string.IsNullOrWhiteSpace(receipt.AuditEvidenceId))
        {
            throw new HttpRequestException("The Audit owner receipt does not match the submitted Job Management event.");
        }
        return receipt.Created;
    }

    private static string RequiredToken(IConfiguration configuration)
    {
        var value = TenantAccessIntegration.Required(configuration, "Monergy:AuditDelivery:Token");
        TenantAccessIntegration.ValidateToken(value);
        return value;
    }

    private sealed record JobAuditReceipt(
        string Recipient,
        string TenantId,
        string SourceEventId,
        string SourceContractId,
        string EventName,
        string EventVersion,
        string Producer,
        string SubjectType,
        string SubjectId,
        string AuditEvidenceId,
        bool Created);
}
