using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Monergy.Services.FinancialRules.Infrastructure;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.FinancialRules.Tests;

public sealed class EvidenceToCalculationTests
{
    [Fact]
    public async Task ActualOwnersConnectEvidenceValidatedFactsFinancialTruthCalculationLineageAndAudit()
    {
        var h = new Harness();
        var content = new ReferenceEvidenceContentStore();
        const string contentRef = "reference://synthetic-d05";
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        content.Seed(contentRef, digest, "text/plain", "INCOME|left|100.00|INR|2026-09-20|synthetic:1|1\nEXPENSE|right|25.00|INR|2026-09-20|synthetic:1|1");
        var evidenceOutbox = new InMemoryEvidenceRepository();
        var evidence = new EvidenceApplication(evidenceOutbox, content, h.Telemetry, h.Clock);
        var created = await evidence.CreateDocumentVersionAsync(Harness.Wire(h.Request(Vs02ContractNames.CreateDocumentVersion,
            new CreateDocumentVersion("document", "document-version", "evidence", Harness.Customer, "MANUAL_UPLOAD",
                "synthetic.txt", "text/plain", contentRef, digest, h.Clock.Now), "version")));
        Assert.Equal(ContractOutcome.Success, created.Outcome);
        // Harness dispatches published owner outboxes; no other service's persistence is queried for business data.
        foreach (var published in evidenceOutbox.DrainOutbox())
        {
            if (published is DomainEvent<EvidenceRegisteredPayload> registered) { await h.Audit.ConsumeAsync(Map(registered)); }
            else if (published is DomainEvent<EvidenceVersionCreatedPayload> version) { await h.Audit.ConsumeAsync(Map(version)); }
        }

        var processingOutbox = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(processingOutbox, new EvidenceReader(evidence, content, h),
            new FixtureDocumentExtractor(), new ReferenceExecutionPolicy(), h.Telemetry, h.Clock);
        var jobs = new JobManagementApplication(new InMemoryJobRepository(), h.Telemetry, h.Clock);
        var job = await jobs.ScheduleJobAsync(Harness.Wire(h.Request(Vs02ContractNames.ScheduleJob,
            new ScheduleJob("job", Vs02ContractNames.ProcessDocument, "Document Intelligence Service", "processing", Harness.Customer, h.Clock.Now), "job-key")));
        Assert.Equal(JobState.Scheduled, job.Data!.State);
        await jobs.StartAsync("job");
        var processed = await processing.ProcessDocumentAsync(Harness.Wire(h.Request(Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing", "document-version", "evidence", Harness.Customer, h.Clock.Now), "process")));
        Assert.Equal(ProcessingState.Completed, processed.Data!.State);
        var status = await processing.GetProcessingStatusAsync(Harness.Wire(h.Request(Vs02ContractNames.GetProcessingStatus, new GetProcessingStatus("processing", Harness.Customer))));
        Assert.Equal(ProcessingState.Completed, status.Data!.State);
        var produced = Harness.Wire(Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(Assert.Single(processingOutbox.DrainOutbox())));
        await h.Audit.ConsumeAsync(Map(produced));
        await h.NormalizeAsync(produced.Payload.Facts, "normalization");

        var result = Harness.Wire(await h.App.ExecuteAsync(h.Execute()));
        Assert.Equal(125m, result.Data!.Value);
        Assert.All(result.Data.Inputs, input =>
        {
            Assert.Equal(produced.Payload.EvidenceId, input.Provenance.EvidenceId);
            Assert.Equal(produced.Payload.DocumentVersionId, input.Provenance.DocumentVersionId);
            Assert.Contains(produced.Payload.Facts, fact => fact.SourceFactId == input.Provenance.SourceFactId);
        });
        var dispatcher = new CalculationOutboxDispatcher(h.Repository, new AuditSink(h.Audit));
        Assert.Equal(1, await dispatcher.DispatchAsync());
        var audit = await h.Audit.ReadAllAsync();
        Assert.Contains(audit, entry => entry.SourceContractId == "CID-040" && entry.SubjectId == result.Data.CalculationResultId);
        Assert.All(audit, entry => Assert.Equal("correlation", entry.CorrelationId));
        await jobs.CompleteAsync("job");
        var jobStatus = await jobs.GetJobStatusAsync(Harness.Wire(h.Request(Vs02ContractNames.GetJobStatus, new GetJobStatus("job", Harness.Customer))));
        Assert.Equal(JobState.Completed, jobStatus.Data!.State);
        Assert.Equal(result.Data.CalculationResultId, (await h.App.ReproduceAsync(h.Explain(result.Data.CalculationResultId))).Data!.CalculationResultId);
    }

    private static AuditableEvent Map<T>(DomainEvent<T> value) =>
        Harness.Wire(new AuditableEvent(value.ContractId, value.EventId, value.EventName, value.EventVersion, value.OccurredAt,
            value.CorrelationId, value.CausationId, value.Producer, value.SubjectType, value.SubjectId));

    private sealed class EvidenceReader(EvidenceApplication owner, ReferenceEvidenceContentStore content, Harness h) : IEvidenceContentReader
    {
        public async Task<EvidenceContent?> ReadAsync(string documentVersionId, string customerId, TrustedSecurityContext security, string correlationId, CancellationToken cancellationToken)
        {
            var request = h.Request(Vs02ContractNames.GetEvidenceReference, new GetEvidenceReference(documentVersionId, customerId), security: security) with { CorrelationId = correlationId };
            var response = Harness.Wire(await owner.GetEvidenceReferenceAsync(Harness.Wire(request), cancellationToken));
            if (response.Outcome != ContractOutcome.Success) { return null; }
            var text = await content.ReadAsync(response.Data!.ContentReference, cancellationToken);
            return text is null ? null : new EvidenceContent(response.Data, text);
        }
    }
}
