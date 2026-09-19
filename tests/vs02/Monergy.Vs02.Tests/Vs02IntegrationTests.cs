using Monergy.Contracts;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class Vs02IntegrationTests
{
    [Fact]
    public async Task FiveBoundaryReferenceFlowPreservesAuthorityProvenanceAndAuditEvidence()
    {
        var telemetry = new CapturingTelemetry();
        var content = new ReferenceEvidenceContentStore();
        content.Seed(Vs02TestContext.ContentReference, Vs02TestContext.ContentSha256, "text/plain", Vs02TestContext.Fixture);
        var evidenceRepository = new InMemoryEvidenceRepository();
        var evidence = new EvidenceApplication(evidenceRepository, content, telemetry, TimeProvider.System);
        var processingRepository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(
            processingRepository,
            new SerializedEvidenceReader(evidence, content),
            new FixtureDocumentExtractor(),
            new ReferenceExecutionPolicy(),
            telemetry,
            TimeProvider.System);
        var financialRepository = new InMemoryFinancialProfileRepository();
        var financial = new FinancialProfileApplication(financialRepository, telemetry, TimeProvider.System);
        var jobs = new JobManagementApplication(new InMemoryJobRepository(), telemetry, TimeProvider.System);
        var audit = new AuditApplication(new AppendOnlyInMemoryAuditRepository(), telemetry, TimeProvider.System);

        var created = await evidence.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload(),
            "request-version-001",
            "idempotency-version-001"));
        Assert.Equal(ContractOutcome.Success, created.Outcome);
        foreach (var source in evidenceRepository.DrainOutbox())
        {
            await ConsumeAsync(audit, source);
        }

        var scheduled = await jobs.ScheduleJobAsync(Vs02TestContext.Request(
            Vs02ContractNames.ScheduleJob,
            new ScheduleJob(
                "job-001",
                Vs02ContractNames.ProcessDocument,
                "Document Intelligence Service",
                "processing-001",
                Vs02TestContext.CustomerId,
                DateTimeOffset.UtcNow),
            "request-job-001",
            "idempotency-job-001"));
        Assert.Equal(JobState.Scheduled, scheduled.Data?.State);
        await jobs.StartAsync("job-001");

        var processed = await processing.ProcessDocumentAsync(Vs02TestContext.Request(
            Vs02ContractNames.ProcessDocument,
            new ProcessDocument(
                "processing-001",
                "document-version-001",
                "evidence-001",
                Vs02TestContext.CustomerId,
                DateTimeOffset.UtcNow),
            "request-processing-001",
            "idempotency-processing-001"));
        Assert.Equal(ProcessingState.Completed, processed.Data?.State);
        foreach (var source in processingRepository.DrainOutbox())
        {
            await ConsumeAsync(audit, source);
        }

        var processingRecord = await processingRepository.GetAsync("processing-001", CancellationToken.None);
        Assert.Equal(2, processingRecord?.Facts.Count);
        var normalized = await financial.NormalizeSourceFactsAsync(Vs02TestContext.Request(
            Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts(
                "financial-profile-001",
                Vs02TestContext.CustomerId,
                "processing-001",
                processingRecord!.Facts,
                "provider-neutral-normalization/1.0.0",
                DateTimeOffset.UtcNow),
            "request-normalize-001",
            "idempotency-normalize-001"));
        Assert.Equal(ContractOutcome.Success, normalized.Outcome);
        Assert.All(normalized.Data!.Facts, fact => Assert.True(fact.Created));
        foreach (var source in financialRepository.DrainOutbox())
        {
            if (source is not DomainEvent<FinancialProfileChangedPayload>)
            {
                await ConsumeAsync(audit, source);
            }
        }

        var provenanceId = normalized.Data.Facts[0].FinancialProvenanceId;
        var provenance = await financial.GetFinancialProvenanceAsync(Vs02TestContext.Request(
            Vs02ContractNames.GetFinancialProvenance,
            new GetFinancialProvenance(provenanceId, Vs02TestContext.CustomerId),
            "request-provenance-001"));
        Assert.Equal("evidence-001", provenance.Data?.EvidenceId);
        Assert.Equal("document-version-001", provenance.Data?.DocumentVersionId);
        Assert.Equal(FixtureDocumentExtractor.ExtractionVersion, provenance.Data?.ExtractionVersion);
        Assert.Equal("actor-001", provenance.Data?.ActorId);
        Assert.Equal(Vs02TestContext.CorrelationId, provenance.Data?.CorrelationId);

        await jobs.CompleteAsync("job-001");
        var jobStatus = await jobs.GetJobStatusAsync(Vs02TestContext.Request(
            Vs02ContractNames.GetJobStatus,
            new GetJobStatus("job-001", Vs02TestContext.CustomerId),
            "request-job-status-001"));
        Assert.Equal(JobState.Completed, jobStatus.Data?.State);

        var auditEvidence = await audit.ReadAllAsync();
        Assert.Equal(5, auditEvidence.Count);
        Assert.All(auditEvidence, record => Assert.Equal(Vs02TestContext.CorrelationId, record.CorrelationId));
        Assert.Contains(telemetry.Signals, signal => signal.Service == "Evidence Service");
        Assert.Contains(telemetry.Signals, signal => signal.Service == "Document Intelligence Service");
        Assert.Contains(telemetry.Signals, signal => signal.Service == "Financial Profile Service");
        Assert.Contains(telemetry.Signals, signal => signal.Service == "Job Management Service");
        Assert.Contains(telemetry.Signals, signal => signal.Service == "Audit Service");
        Assert.DoesNotContain(telemetry.Signals, signal => signal.ToString()?.Contains("125000", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(telemetry.Signals, signal => signal.ToString()?.Contains("financial-statement", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DownstreamEvidenceFailureDoesNotCreateFinancialTruth()
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(
            repository,
            new UnavailableEvidenceReader(),
            new FixtureDocumentExtractor(),
            new ReferenceExecutionPolicy(),
            new CapturingTelemetry(),
            TimeProvider.System);
        var result = await processing.ProcessDocumentAsync(Vs02TestContext.Request(
            Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-failed", "missing-version", "missing-evidence", Vs02TestContext.CustomerId, DateTimeOffset.UtcNow),
            "request-processing-failed",
            "idempotency-processing-failed"));

        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        Assert.Equal(ContractErrorCategory.DependencyFailure, result.Error?.Category);
        Assert.True(result.Error?.Retryable);
        Assert.Empty(repository.DrainOutbox());
    }

    [Fact]
    public async Task RetryableEvidenceFailureCanResumeWithoutDuplicatingTheProcessingIdentity()
    {
        var repository = new InMemoryDocumentProcessingRepository();
        var reader = new TransientEvidenceReader();
        var processing = new DocumentProcessingApplication(
            repository,
            reader,
            new FixtureDocumentExtractor(),
            new ReferenceExecutionPolicy(),
            new CapturingTelemetry(),
            TimeProvider.System);
        var request = Vs02TestContext.Request(
            Vs02ContractNames.ProcessDocument,
            new ProcessDocument(
                "processing-retry",
                "document-version-001",
                "evidence-001",
                Vs02TestContext.CustomerId,
                DateTimeOffset.UtcNow),
            "request-processing-retry",
            "idempotency-processing-retry");

        var failed = await processing.ProcessDocumentAsync(request);
        Assert.Equal(ContractOutcome.Failed, failed.Outcome);
        Assert.True(failed.Error?.Retryable);
        Assert.Empty(repository.DrainOutbox());

        var completed = await processing.ProcessDocumentAsync(request);
        Assert.Equal(ContractOutcome.Success, completed.Outcome);
        Assert.Equal(ProcessingState.Completed, completed.Data?.State);
        Assert.Single(repository.DrainOutbox());
        Assert.Equal(2, reader.Attempts);
    }

    [Fact]
    public async Task NormalizationReplayIsIdempotentAndNewSourceCreatesAnUpdateEvent()
    {
        var repository = new InMemoryFinancialProfileRepository();
        var app = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var fact = CreateFact("source-fact-001", 100m);
        var request = NormalizeRequest(fact, "request-normalize-1", "idempotency-normalize-1");

        var created = await app.NormalizeSourceFactsAsync(request);
        repository.DrainOutbox();
        var replayed = await app.NormalizeSourceFactsAsync(request);
        Assert.False(replayed.Data!.Facts[0].Created);
        Assert.Empty(repository.DrainOutbox());

        var updated = await app.NormalizeSourceFactsAsync(
            NormalizeRequest(CreateFact("source-fact-002", 110m), "request-normalize-2", "idempotency-normalize-2"));
        Assert.Equal(created.Data!.Facts[0].FinancialFactId, updated.Data!.Facts[0].FinancialFactId);
        Assert.Equal(2, updated.Data.Facts[0].Revision);
        Assert.Contains(repository.DrainOutbox(), item =>
            item is DomainEvent<FinancialFactChangedPayload> changed && changed.ContractId == "CID-035");
    }

    private static ValidatedSourceFact CreateFact(string sourceFactId, decimal value) =>
        new(
            sourceFactId,
            Vs02TestContext.CustomerId,
            "INCOME",
            "Salary",
            value,
            "INR",
            new DateOnly(2026, 8, 31),
            "page:1",
            0.98m,
            "evidence-001",
            "document-version-001",
            FixtureDocumentExtractor.ExtractionVersion,
            FixtureDocumentExtractor.ValidationVersion);

    private static ContractRequest<NormalizeSourceFacts> NormalizeRequest(
        ValidatedSourceFact fact,
        string requestId,
        string idempotencyKey) =>
        Vs02TestContext.Request(
            Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts(
                "financial-profile-001",
                Vs02TestContext.CustomerId,
                "processing-001",
                [fact],
                "provider-neutral-normalization/1.0.0",
                DateTimeOffset.UtcNow),
            requestId,
            idempotencyKey);

    private static Task<AuditEvidenceRecord> ConsumeAsync(AuditApplication audit, object source) => source switch
    {
        DomainEvent<EvidenceRegisteredPayload> value => audit.ConsumeAsync(AuditEventMapper.From(value)),
        DomainEvent<EvidenceVersionCreatedPayload> value => audit.ConsumeAsync(AuditEventMapper.From(value)),
        DomainEvent<ValidatedSourceFactsProducedPayload> value => audit.ConsumeAsync(AuditEventMapper.From(value)),
        DomainEvent<FinancialFactChangedPayload> value => audit.ConsumeAsync(AuditEventMapper.From(value)),
        _ => throw new InvalidOperationException("Unexpected outbox event type."),
    };

    private sealed class UnavailableEvidenceReader : IEvidenceContentReader
    {
        public Task<EvidenceContent?> ReadAsync(
            string documentVersionId,
            string customerId,
            TrustedSecurityContext security,
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<EvidenceContent?>(null);
    }

    private sealed class TransientEvidenceReader : IEvidenceContentReader
    {
        public int Attempts { get; private set; }

        public Task<EvidenceContent?> ReadAsync(
            string documentVersionId,
            string customerId,
            TrustedSecurityContext security,
            string correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1)
            {
                return Task.FromResult<EvidenceContent?>(null);
            }

            var reference = new EvidenceReference(
                "evidence-001",
                "document-001",
                documentVersionId,
                customerId,
                Vs02TestContext.ContentReference,
                Vs02TestContext.ContentSha256,
                "text/plain");
            return Task.FromResult<EvidenceContent?>(new EvidenceContent(reference, Vs02TestContext.Fixture));
        }
    }
}
