namespace Monergy.UatGateway;

internal sealed record ServiceBoundary(string Id, string Label, string Url, string HealthPath, string Implementation);
internal sealed record ServiceState(string Id, string Label, string Implementation, bool Available);
internal static class Topology
{
    public static readonly ServiceBoundary[] Services = [
        new("access-management", "Access Management", "http://127.0.0.1:5088", "/health/ready", "Durable tenant policy, administration and outbox"),
        new("customer-identity", "Customer & Identity", "http://127.0.0.1:5101", "/internal/health/ready", "Durable local sessions and identity provisioning"),
        new("audit", "Audit", "http://127.0.0.1:5102", "/internal/health/ready", "Durable canonical access audit"),
        new("consent", "Consent", "http://127.0.0.1:5110", "/health/ready", "Service scaffold; no live consent journey"),
        new("evidence", "Evidence", "http://127.0.0.1:5111", "/health/ready", "Guarded reference business adapters"),
        new("document-intelligence", "Document Intelligence", "http://127.0.0.1:5112", "/health/ready", "Worker boundary; business consumer not connected"),
        new("financial-profile", "Financial Profile", "http://127.0.0.1:5113", "/health/ready", "Guarded reference business adapters"),
        new("financial-rules", "Financial Rules", "http://127.0.0.1:5114", "/health/ready", "Guarded reference business adapters"),
        new("search-retrieval", "Search & Retrieval", "http://127.0.0.1:5115", "/health/ready", "Guarded reference business adapters"),
        new("reporting", "Reporting", "http://127.0.0.1:5116", "/health/ready", "Guarded reference business adapters; report storage is volatile"),
        new("integration-gateway", "Integration Gateway", "http://127.0.0.1:5117", "/health/ready", "Guarded provider simulation"),
        new("job-management", "Job Management", "http://127.0.0.1:5118", "/health/ready", "Worker boundary; business consumer not connected"),
        new("ai-intelligence", "AI Intelligence", "http://127.0.0.1:5119", "/health/ready", "Service scaffold; no live AI journey"),
    ];
    public static Task<ServiceState[]> ReadAsync(HttpClient client, CancellationToken ct) => Task.WhenAll(Services.Select(async service =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var available = false;
        try { using var response = await client.GetAsync(service.Url + service.HealthPath, timeout.Token).ConfigureAwait(false); available = response.IsSuccessStatusCode; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        return new ServiceState(service.Id, service.Label, service.Implementation, available);
    }));
}
