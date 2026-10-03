using Monergy.Contracts;
using Monergy.Services.Audit.Infrastructure;
using Monergy.Services.DocumentIntelligence.Application;
using Xunit;

namespace Monergy.DocumentReprocessing.Tests;

public sealed class FailureSecurityAndAtomicityTests
{
    [Fact]
    public async Task UnknownNonterminalAndOtherCustomerPredecessorsFailWithoutFabricatedEvents()
    {
        var unknown = new D12Harness();
        Assert.Equal(
            ContractErrorCategory.NotFound,
            (await unknown.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest())).Error?.Category);
        Assert.Empty(unknown.Repository.OutboxSnapshot());

        var nonterminal = new D12Harness();
        await nonterminal.Repository.AcceptAsync(Candidate("accepted-predecessor"), CancellationToken.None);
        Assert.Equal(
            ContractErrorCategory.PreconditionFailed,
            (await nonterminal.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
                previousProcessingId: "accepted-predecessor"))).Error?.Category);
        Assert.Empty(nonterminal.Repository.OutboxSnapshot());

        var otherCustomer = new D12Harness();
        await otherCustomer.CreateFailedPredecessorAsync();
        var denied = D12TestContext.ReprocessRequest(
            customerId: D12TestContext.OtherCustomerId,
            security: D12TestContext.Security(D12TestContext.OtherCustomerId));
        Assert.Equal(
            ContractErrorCategory.NotFound,
            (await otherCustomer.Application.ReprocessDocumentAsync(denied)).Error?.Category);
        Assert.Empty(otherCustomer.Repository.OutboxSnapshot());
    }

    [Theory]
    [InlineData(ContractErrorCategory.AccessDenied, "security.execution.denied")]
    [InlineData(ContractErrorCategory.ConsentRequired, "consent.required")]
    [InlineData(ContractErrorCategory.ConsentExpired, "consent.expired")]
    [InlineData(ContractErrorCategory.ConsentRevoked, "consent.revoked")]
    public async Task ExecutionTimePolicyIsRecheckedAndClassifiedWithoutExtraction(
        ContractErrorCategory category,
        string code)
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var extractionBaseline = harness.Extractor.Calls;
        harness.Policy.Enqueue(null);
        harness.Policy.Enqueue(new ContractError(code, category, "safe policy denial", false, "correlation-d12"));

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());

        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        Assert.Equal(category, result.Error?.Category);
        Assert.Equal(extractionBaseline, harness.Extractor.Calls);
        var failed = Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
        Assert.Equal(category, failed.Payload.ErrorCategory);
        Assert.Equal(code, failed.Payload.FailureCode);
        Assert.DoesNotContain(D12TestContext.ValidContent, failed.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContractErrorCategory.AccessDenied, "security.execution.denied")]
    [InlineData(ContractErrorCategory.ConsentExpired, "consent.expired")]
    [InlineData(ContractErrorCategory.ConsentRevoked, "consent.revoked")]
    public async Task SameCustomerReplayAndStatusRequireCurrentAccessWithoutMintingFailureFacts(
        ContractErrorCategory category,
        string code)
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var request = D12TestContext.ReprocessRequest();
        Assert.Equal(ContractOutcome.Success, (await harness.Application.ReprocessDocumentAsync(request)).Outcome);
        var terminalIntentCount = harness.Repository.EventHistorySnapshot().Count;

        var denial = new ContractError(code, category, "current reference policy denied access", false, "correlation-d12");
        harness.Policy.Enqueue(denial);
        var replay = await harness.Application.ReprocessDocumentAsync(request with { RequestId = "denied-replay" });
        harness.Policy.Enqueue(denial);
        var status = await harness.Application.GetProcessingStatusAsync(StatusRequest());

        Assert.Equal(ContractOutcome.Rejected, replay.Outcome);
        Assert.Equal(category, replay.Error?.Category);
        Assert.Equal(ContractOutcome.Rejected, status.Outcome);
        Assert.Equal(category, status.Error?.Category);
        Assert.Equal(terminalIntentCount, harness.Repository.EventHistorySnapshot().Count);
    }

    [Fact]
    public async Task MalformedOrNullStatusPayloadFailsSafelyBeforeOwnerLookup()
    {
        var harness = new D12Harness();
        var nullPayload = StatusRequest() with { Payload = null! };
        var malformed = StatusRequest() with { Payload = new GetProcessingStatus(string.Empty, D12TestContext.CustomerId) };

        Assert.Equal(
            ContractErrorCategory.ValidationError,
            (await harness.Application.GetProcessingStatusAsync(nullPayload)).Error?.Category);
        Assert.Equal(
            ContractErrorCategory.ValidationError,
            (await harness.Application.GetProcessingStatusAsync(malformed)).Error?.Category);
        Assert.Equal(0, harness.Policy.Calls);
    }

    [Fact]
    public async Task ConsentRevokedDuringEvidenceReadFailsBeforeExtraction()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var readBaseline = harness.Reader.Calls;
        var extractionBaseline = harness.Extractor.Calls;
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.BeforeReadAsync = async (call, cancellationToken) =>
        {
            if (call == readBaseline + 2)
            {
                readEntered.TrySetResult();
                await releaseRead.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        };

        var operation = harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Policy.Enqueue(new ContractError(
            "consent.revoked",
            ContractErrorCategory.ConsentRevoked,
            "Consent was revoked during the Evidence read.",
            false,
            "correlation-d12"));
        releaseRead.TrySetResult();

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        Assert.Equal(ContractErrorCategory.ConsentRevoked, result.Error?.Category);
        Assert.Equal(extractionBaseline, harness.Extractor.Calls);
        var failed = Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
        Assert.Equal(ContractErrorCategory.ConsentRevoked, failed.Payload.ErrorCategory);
    }

    [Fact]
    public async Task DependencyMissingSourceEmptyFactsAndExtractorFailureHaveDistinctSafeEvents()
    {
        await AssertFailureAsync(
            configure: harness =>
            {
                harness.Reader.Enqueue(harness.Reader.Required(D12TestContext.Version1));
                harness.Reader.EnqueueDependencyFailure();
            },
            expectedCode: "processing.evidence.dependency-unavailable",
            expectedCategory: ContractErrorCategory.DependencyFailure,
            retryable: true);

        await AssertFailureAsync(
            configure: harness =>
            {
                harness.Reader.Enqueue(harness.Reader.Required(D12TestContext.Version1));
                harness.Reader.Enqueue(null);
            },
            expectedCode: "processing.evidence.unavailable",
            expectedCategory: ContractErrorCategory.DependencyFailure,
            retryable: true);

        await AssertFailureAsync(
            configure: harness =>
            {
                harness.Reader.Enqueue(harness.Reader.Required(D12TestContext.Version1));
                var source = harness.Reader.Required(D12TestContext.Version1);
                harness.Reader.Enqueue(source with { Content = D12TestContext.EmptyContent });
            },
            expectedCode: "processing.no-valid-facts",
            expectedCategory: ContractErrorCategory.ProcessingFailed,
            retryable: false);

        await AssertFailureAsync(
            configure: harness => harness.Extractor.ThrowControlledFailure = true,
            expectedCode: "processing.extraction.failed",
            expectedCategory: ContractErrorCategory.ProcessingFailed,
            retryable: false);
    }

    [Fact]
    public async Task TerminalStateAndEventIntentCommitAtomicallyAndStaleAttemptsCannotContradictOutcome()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        harness.Repository.FailNextTerminalCommit();

        await Assert.ThrowsAsync<ProcessingAtomicCommitException>(() =>
            harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest()));
        var running = await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None);

        Assert.Equal(ProcessingState.Running, running?.State);
        Assert.Empty(harness.Repository.OutboxSnapshot());
        await Assert.ThrowsAsync<ProcessingConflictException>(() => harness.Repository.CommitTerminalAsync(
            D12TestContext.NewProcessingId,
            "stale-attempt",
            ProcessingState.Completed,
            harness.Clock.Now,
            [],
            null,
            null,
            false,
            new object(),
            CancellationToken.None));
        Assert.Equal(ProcessingState.Running, (await harness.Repository.GetAsync(
            D12TestContext.NewProcessingId, CancellationToken.None))?.State);
        Assert.Empty(harness.Repository.OutboxSnapshot());
    }

    [Fact]
    public async Task CancellationAfterAdmissionLeavesTruthfulQueryableStateAndNoBlindNewOperation()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        using var cancellation = new CancellationTokenSource();
        harness.Extractor.BeforeExtract = _ => cancellation.Cancel();
        var request = D12TestContext.ReprocessRequest();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Application.ReprocessDocumentAsync(request, cancellation.Token));

        var retained = await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None);
        Assert.Equal(ProcessingState.Running, retained?.State);
        Assert.Empty(harness.Repository.OutboxSnapshot());
        harness.Extractor.BeforeExtract = null;
        var reconciled = await harness.Application.ReprocessDocumentAsync(request with { RequestId = "reconcile-known-operation" });
        Assert.Equal(ContractOutcome.Success, reconciled.Outcome);
        Assert.Equal(ProcessingState.Running, reconciled.Data?.State);
        Assert.Empty(harness.Repository.OutboxSnapshot());
    }

    [Fact]
    public async Task FailureEventIsAuditCompatibleButAuditAvailabilityDoesNotControlOwnerCommit()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        harness.Extractor.ThrowControlledFailure = true;
        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());

        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        var source = Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
        Assert.Equal(ProcessingState.Failed, (await harness.Repository.GetAsync(
            D12TestContext.NewProcessingId, CancellationToken.None))?.State);

        var audit = new AppendOnlyInMemoryAuditRepository();
        var mapped = new AuditableEvent(
            source.ContractId,
            source.EventId,
            source.EventName,
            source.EventVersion,
            source.OccurredAt,
            source.CorrelationId,
            source.CausationId,
            source.Producer,
            source.SubjectType,
            source.SubjectId);
        var first = await audit.AppendAsync(mapped, harness.Clock.Now, CancellationToken.None);
        var replay = await audit.AppendAsync(mapped, harness.Clock.Now, CancellationToken.None);
        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Single(await audit.ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingProcessDocumentFailureNowCommitsOneTypedFailureIntentAndRetryRemainsDistinct()
    {
        var harness = new D12Harness();
        harness.Reader.Seed(D12TestContext.Version1, content: D12TestContext.EmptyContent);
        var request = D12TestContext.ProcessRequest("legacy-processing", D12TestContext.Version1);

        var failed = await harness.Application.ProcessDocumentAsync(request);
        Assert.Equal(ContractOutcome.Failed, failed.Outcome);
        var failure = Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(
            Assert.Single(harness.Repository.DrainOutbox()));
        Assert.Equal("legacy-processing:attempt:1", failure.Payload.AttemptId);

        harness.Reader.Seed(D12TestContext.Version1);
        var nonRetryableReplay = await harness.Application.ProcessDocumentAsync(request);
        Assert.Equal(ContractOutcome.Success, nonRetryableReplay.Outcome);
        Assert.Equal(ProcessingState.Failed, nonRetryableReplay.Data?.State);
        Assert.Empty(harness.Repository.OutboxSnapshot());
    }

    private static ProcessingRecord Candidate(string processingId) => new(
        processingId,
        D12TestContext.Version1,
        D12TestContext.EvidenceId,
        D12TestContext.CustomerId,
        D12ContractNames.ReprocessDocument,
        ContractGuard.CurrentVersion,
        $"key-{processingId}",
        $"fingerprint-{processingId}",
        null,
        null,
        null,
        ProcessingState.Accepted,
        null,
        null,
        false,
        0,
        null,
        null,
        D12TestContext.Now,
        []);

    private static ContractRequest<GetProcessingStatus> StatusRequest() => new(
        Vs02ContractNames.GetProcessingStatus,
        ContractGuard.CurrentVersion,
        "request-status-d12",
        "correlation-d12",
        null,
        D12TestContext.Security(),
        null,
        new GetProcessingStatus(D12TestContext.NewProcessingId, D12TestContext.CustomerId));

    private static async Task AssertFailureAsync(
        Action<D12Harness> configure,
        string expectedCode,
        ContractErrorCategory expectedCategory,
        bool retryable)
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        configure(harness);

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());

        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(expectedCategory, result.Error?.Category);
        Assert.Equal(retryable, result.Error?.Retryable);
        var failed = Assert.IsType<DomainEvent<DocumentProcessingFailedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
        Assert.Equal(expectedCode, failed.Payload.FailureCode);
        Assert.Equal(expectedCategory, failed.Payload.ErrorCategory);
        Assert.Equal(retryable, failed.Payload.Retryable);
        Assert.Equal(D12TestContext.PreviousProcessingId, failed.Payload.PreviousProcessingId);
    }
}
