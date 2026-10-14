using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class JobAuditDeliveryTests
{
    [Fact]
    public async Task Governed_consumer_resolves_tenant_and_validates_owner_receipt()
    {
        var repository = new InMemoryJobRepository();
        var request = JobTestContext.Schedule("job-audit-delivery", "job-audit-key");
        var application = new JobManagementApplication(repository, new CapturingTelemetry(),
            new MutableTimeProvider(JobTestContext.Now));
        var scheduled = await application.ScheduleJobAsync(request);
        Assert.Equal(ContractOutcome.Success, scheduled.Outcome);

        var source = new AuditableEvent("CID-058", "job-audit-delivery:CID-058:1:0",
            Vs02ContractNames.JobStarted, ContractGuard.CurrentVersion, JobTestContext.Now,
            request.CorrelationId, request.CausationId, "Job Management Service", "processing-job",
            "job-audit-delivery");
        var handler = new ReceiptHandler(source);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5102/") };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:AuditDelivery:Token"] = new string('a', 64),
        }).Build();
        var tenants = new TenantBoundaryOptions
        {
            Service = "job-management",
            CapabilityPrefix = "job-management.",
            CustomerTenants = new Dictionary<string, string> { [JobTestContext.CustomerId] = "T001" },
            ProbeUrl = "http://127.0.0.1:5118/",
        };
        var consumer = new HttpJobAuditEventConsumer(client, configuration, tenants, repository);

        Assert.True(await consumer.ConsumeAsync(source, CancellationToken.None));
        Assert.Equal("T001", handler.Tenant);
        Assert.Equal(new string('a', 64), handler.Token);
    }

    private sealed class ReceiptHandler(AuditableEvent expected) : HttpMessageHandler
    {
        public string? Tenant { get; private set; }
        public string? Token { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/reference/events/job-audit", request.RequestUri?.AbsolutePath);
            Tenant = request.Headers.GetValues("X-Monergy-Reference-Tenant").Single();
            Token = request.Headers.GetValues("X-Monergy-Reference-Transport").Single();
            var submitted = JsonSerializer.Deserialize<AuditableEvent>(
                await request.Content!.ReadAsStringAsync(cancellationToken), ContractJson.Options);
            Assert.Equal(expected, submitted);
            var receipt = new
            {
                recipient = "Audit Service LOCAL/UAT composition",
                tenantId = Tenant,
                sourceEventId = expected.EventId,
                sourceContractId = expected.ContractId,
                expected.EventName,
                expected.EventVersion,
                expected.Producer,
                expected.SubjectType,
                expected.SubjectId,
                auditEvidenceId = "audit-" + expected.EventId,
                created = true,
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(receipt, ContractJson.Options),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
