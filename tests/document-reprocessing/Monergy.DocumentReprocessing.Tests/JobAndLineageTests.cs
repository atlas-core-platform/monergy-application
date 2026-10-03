using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.DocumentReprocessing.Tests;

public sealed class JobAndLineageTests
{
    [Fact]
    public async Task GovernedJobSchedulingDeliveryAndStatusInvokeReprocessingOwnerBehavior()
    {
        var document = new D12Harness();
        await document.CreateFailedPredecessorAsync();
        var jobs = new JobManagementApplication(new InMemoryJobRepository(), new D12Telemetry(), document.Clock);
        var schedule = JobRequest("job-d12", D12TestContext.NewProcessingId);

        var scheduled = await jobs.ScheduleJobAsync(schedule);
        Assert.Equal(JobState.Scheduled, scheduled.Data?.State);
        var executed = await jobs.ExecuteAsync(
            "job-d12",
            new AllowJobPolicy(),
            new AllowJobPolicy(),
            new ReprocessJobTarget(document.Application),
            CancellationToken.None);
        var status = await jobs.GetJobStatusAsync(new ContractRequest<GetJobStatus>(
            Vs02ContractNames.GetJobStatus,
            ContractGuard.CurrentVersion,
            "request-job-status-d12",
            "correlation-d12",
            null,
            D12TestContext.Security(),
            null,
            new GetJobStatus("job-d12", D12TestContext.CustomerId)));

        Assert.Equal(ContractOutcome.Success, executed.Outcome);
        Assert.Equal(JobState.Completed, status.Data?.State);
        Assert.Equal(ProcessingState.Completed, (await document.Repository.GetAsync(
            D12TestContext.NewProcessingId, CancellationToken.None))?.State);
    }

    [Fact]
    public async Task MechanicalJobOutcomeDoesNotMisrepresentDocumentBusinessFailure()
    {
        var document = new D12Harness();
        await document.CreateFailedPredecessorAsync();
        document.Extractor.ThrowControlledFailure = true;
        var jobs = new JobManagementApplication(new InMemoryJobRepository(), new D12Telemetry(), document.Clock);
        await jobs.ScheduleJobAsync(JobRequest("job-d12-failure", D12TestContext.NewProcessingId));

        var executed = await jobs.ExecuteAsync(
            "job-d12-failure",
            new AllowJobPolicy(),
            new AllowJobPolicy(),
            new ReprocessJobTarget(document.Application),
            CancellationToken.None);

        Assert.Equal(ContractOutcome.Failed, executed.Outcome);
        Assert.Equal(JobState.Failed, executed.Data?.State);
        Assert.Equal(ProcessingState.Failed, (await document.Repository.GetAsync(
            D12TestContext.NewProcessingId, CancellationToken.None))?.State);
    }

    [Fact]
    public async Task SuccessfulFactsRetainExactSourceAndProcessingLineageWithoutFinancialAuthority()
    {
        var harness = new D12Harness();
        await harness.CreateCompletedPredecessorAsync();
        harness.Reader.Seed(D12TestContext.Version2);

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
            versionId: D12TestContext.Version2,
            reason: ReprocessingReason.UpdatedEvidence));

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        var source = Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
        Assert.Equal(D12TestContext.NewProcessingId, source.Payload.ProcessingId);
        Assert.Equal(D12TestContext.EvidenceId, source.Payload.EvidenceId);
        Assert.Equal(D12TestContext.Version2, source.Payload.DocumentVersionId);
        Assert.All(source.Payload.Facts, fact =>
        {
            Assert.Equal(D12TestContext.NewProcessingId, source.Payload.ProcessingId);
            Assert.Equal(D12TestContext.EvidenceId, fact.EvidenceId);
            Assert.Equal(D12TestContext.Version2, fact.DocumentVersionId);
            Assert.Equal("fixture-extractor/1.0.0", fact.ExtractionVersion);
            Assert.Equal("canonical-source-fact/1.0.0", fact.ValidationVersion);
        });
        Assert.DoesNotContain("FinancialProfile", source.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusVisibilityRemainsCustomerScopedForReprocessingOperations()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());

        var denied = await harness.Application.GetProcessingStatusAsync(new ContractRequest<GetProcessingStatus>(
            Vs02ContractNames.GetProcessingStatus,
            ContractGuard.CurrentVersion,
            "status-other",
            "correlation-d12",
            null,
            D12TestContext.Security(D12TestContext.OtherCustomerId),
            null,
            new GetProcessingStatus(D12TestContext.NewProcessingId, D12TestContext.OtherCustomerId)));

        Assert.Equal(ContractOutcome.Rejected, denied.Outcome);
        Assert.Equal(ContractErrorCategory.NotFound, denied.Error?.Category);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionCreatesNoOperationOrTerminalIntent()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(), cancellation.Token));

        Assert.Null(await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None));
        Assert.Empty(harness.Repository.OutboxSnapshot());
    }

    [Fact]
    public async Task TimedOutClaimWithCancellationRequiresBoundedSameIdentityReconciliation()
    {
        var document = new D12Harness();
        var repository = new InMemoryJobRepository();
        var jobs = new JobManagementApplication(repository, new D12Telemetry(), document.Clock);
        await jobs.ScheduleJobAsync(JobRequest("job-d12-timeout", D12TestContext.NewProcessingId));
        var lease = Assert.IsType<JobLease>(await repository.ClaimAsync(
            "job-d12-timeout",
            "worker-d12",
            document.Clock.Now,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
        var cancellation = await jobs.CancelJobAsync(new ContractRequest<CancelJob>(
            Vs02ContractNames.CancelJob,
            ContractGuard.CurrentVersion,
            "cancel-job-d12-timeout",
            "correlation-d12",
            null,
            D12TestContext.Security(),
            null,
            new CancelJob(lease.Job.JobId, D12TestContext.CustomerId, "bounded delivery timeout")));
        Assert.Equal(JobState.CancellationRequested, cancellation.Data?.State);

        document.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await jobs.RecoverInterruptedAsync());
        var unknown = Assert.IsType<DurableJobRecord>(await repository.GetAsync(lease.Job.JobId, CancellationToken.None));
        Assert.Equal(JobState.CancellationRequested, unknown.State);
        Assert.True(unknown.ReconciliationRequired);
        Assert.Equal("job.cancellation.outcome-unknown", unknown.FailureCode);
        var attempt = Assert.Single(await repository.GetAttemptsAsync(lease.Job.JobId, CancellationToken.None));
        Assert.Equal("CANCELLATION_OUTCOME_UNKNOWN", attempt.Outcome);
        Assert.NotNull(attempt.EndedAt);

        var reconciled = await jobs.ReconcileAsync(
            lease.Job.JobId,
            D12TestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect);
        Assert.Equal(lease.Job.JobId, reconciled.JobId);
        Assert.Equal(JobState.Scheduled, reconciled.State);
        Assert.False(reconciled.ReconciliationRequired);
        Assert.Equal(0, reconciled.ReplayCount);
    }

    private static ContractRequest<ScheduleJob> JobRequest(string jobId, string processingId) => new(
        Vs02ContractNames.ScheduleJob,
        ContractGuard.CurrentVersion,
        $"request-{jobId}",
        "correlation-d12",
        null,
        D12TestContext.Security(),
        $"key-{jobId}",
        new ScheduleJob(
            jobId,
            D12ContractNames.ReprocessDocument,
            "Document Intelligence Service",
            processingId,
            D12TestContext.CustomerId,
            D12TestContext.Now));

    private sealed class ReprocessJobTarget(DocumentProcessingApplication application) : IJobExecutionTarget
    {
        public async Task<JobExecutionResult> ExecuteAsync(DurableJobRecord job, CancellationToken cancellationToken)
        {
            var request = D12TestContext.ReprocessRequest(
                processingId: job.PayloadReference,
                idempotencyKey: $"{job.IdempotencyKey}-owner",
                security: job.Security) with
            {
                RequestId = job.RequestId,
                CorrelationId = job.CorrelationId,
                CausationId = job.CausationId,
            };
            var result = await application.ReprocessDocumentAsync(request, cancellationToken);
            if (result.Outcome == ContractOutcome.Success && result.Data?.State == ProcessingState.Completed)
            {
                return JobExecutionResult.Completed(result.Data.ProcessingId);
            }

            if (result.Outcome == ContractOutcome.Failed || result.Data?.State == ProcessingState.Failed)
            {
                return JobExecutionResult.Failed(
                    result.Error?.Code ?? result.Data?.FailureCode ?? "document.reprocessing.failed",
                    result.Error?.Retryable ?? result.Data?.Retryable ?? false);
            }

            throw new InvalidOperationException("The owner outcome is not terminal and requires Job reconciliation.");
        }
    }

    private sealed class AllowJobPolicy : IJobAuthorizationPolicy, IJobConsentDecisionPort
    {
        public Task<ContractError?> AuthorizeAsync(
            TrustedSecurityContext context,
            DurableJobRecord job,
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<ContractError?>(null);

        public Task<ContractError?> EvaluateAsync(
            TrustedSecurityContext context,
            DurableJobRecord job,
            string correlationId,
            CancellationToken cancellationToken) => Task.FromResult<ContractError?>(null);
    }
}
