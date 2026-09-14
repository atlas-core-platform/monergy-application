using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class StateAndSecurityTests
{
    [Fact]
    public void ReferenceAdaptersFailClosedOutsideLocalOrCi()
    {
        var uat = new ConfigurationManager { ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "UAT" };
        var local = new ConfigurationManager { ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "LOCAL" };

        Assert.Throws<InvalidOperationException>(() => ReferenceAdapterGuard.EnsureAllowed(uat));
        ReferenceAdapterGuard.EnsureAllowed(local);
    }

    [Fact]
    public async Task JobStateMachineIsIdempotentAndRejectsInvalidTransitions()
    {
        var repository = new InMemoryJobRepository();
        var app = new JobManagementApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var request = Vs02TestContext.Request(
            Vs02ContractNames.ScheduleJob,
            new ScheduleJob("job-001", Vs02ContractNames.ProcessDocument, "Document Intelligence Service", "processing-001", Vs02TestContext.CustomerId, DateTimeOffset.UtcNow),
            "request-job-001",
            "idempotency-job-001");
        var first = await app.ScheduleJobAsync(request);
        var replay = await app.ScheduleJobAsync(request);

        Assert.Equal(first.Data, replay.Data);
        await app.StartAsync("job-001");
        await app.CompleteAsync("job-001");
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync("job-001"));
    }

    [Fact]
    public async Task RevokedConsentStopsDelayedDocumentExecution()
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var policy = new ReferenceExecutionPolicy();
        policy.Revoke("consent-001");
        var app = new DocumentProcessingApplication(
            repository,
            new MissingEvidenceReader(),
            new FixtureDocumentExtractor(),
            policy,
            new CapturingTelemetry(),
            TimeProvider.System);
        var request = Vs02TestContext.Request(
            Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-revoked", "document-version-001", "evidence-001", Vs02TestContext.CustomerId, DateTimeOffset.UtcNow),
            "request-processing-revoked",
            "idempotency-processing-revoked");

        var result = await app.ProcessDocumentAsync(request);

        Assert.Equal(ContractErrorCategory.ConsentRevoked, result.Error?.Category);
        Assert.Null(await repository.GetAsync("processing-revoked", CancellationToken.None));
    }

    private sealed class MissingEvidenceReader : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(
            string documentVersionId,
            string customerId,
            TrustedSecurityContext security,
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(null);
    }
}
