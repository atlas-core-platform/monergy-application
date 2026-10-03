using Monergy.Contracts;
using Xunit;

namespace Monergy.DocumentReprocessing.Tests;

public sealed class ReprocessingBehaviorTests
{
    [Fact]
    public async Task FailedSameVersionReprocessingPreservesPredecessorAndCapturesImmutableLinkage()
    {
        var harness = new D12Harness();
        var before = await harness.CreateFailedPredecessorAsync();
        var predecessorHistory = harness.Repository.EventHistorySnapshot().ToArray();

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        Assert.Equal(ProcessingState.Completed, result.Data?.State);
        Assert.Equal(D12TestContext.PreviousProcessingId, result.Data?.PreviousProcessingId);
        var predecessor = await harness.Repository.GetAsync(D12TestContext.PreviousProcessingId, CancellationToken.None);
        var reprocessed = await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None);
        Assert.Equal(before, predecessor);
        Assert.Equal(D12TestContext.PreviousProcessingId, reprocessed?.PreviousProcessingId);
        Assert.Equal(ProcessingState.Failed, reprocessed?.PredecessorSnapshot?.State);
        Assert.Equal(before.UpdatedAt, reprocessed?.PredecessorSnapshot?.UpdatedAt);
        Assert.Equal(before.TerminalEventId, reprocessed?.PredecessorSnapshot?.TerminalEventId);
        Assert.Equal(predecessorHistory, harness.Repository.EventHistorySnapshot().Take(predecessorHistory.Length));
        Assert.Single(reprocessed!.Facts);
        Assert.Equal(D12TestContext.Version1, reprocessed.Facts[0].DocumentVersionId);
    }

    [Fact]
    public async Task FailedSameVersionRecoveryUsesUnchangedEvidenceOwnedBytesAfterControlledDependencyFailure()
    {
        var harness = new D12Harness();
        harness.Reader.Seed(D12TestContext.Version1);
        var beforeFailure = harness.Reader.Required(D12TestContext.Version1);
        harness.Reader.EnqueueDependencyFailure();

        var failed = await harness.Application.ProcessDocumentAsync(D12TestContext.ProcessRequest(
            D12TestContext.PreviousProcessingId,
            D12TestContext.Version1));
        Assert.Equal(ContractOutcome.Failed, failed.Outcome);
        harness.Repository.DrainOutbox();
        var beforeReprocessing = harness.Reader.Required(D12TestContext.Version1);

        var recovered = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());
        var afterReprocessing = harness.Reader.Required(D12TestContext.Version1);

        Assert.Equal(ContractOutcome.Success, recovered.Outcome);
        Assert.Equal(beforeFailure.Reference, beforeReprocessing.Reference);
        Assert.Equal(beforeFailure.Content, beforeReprocessing.Content);
        Assert.Equal(beforeReprocessing, afterReprocessing);
    }

    [Fact]
    public async Task LaterLegacyRetryCannotRewriteCapturedPredecessorOutcome()
    {
        var harness = new D12Harness();
        var processRequest = D12TestContext.ProcessRequest(
            D12TestContext.PreviousProcessingId,
            D12TestContext.Version1);
        var failed = await harness.Application.ProcessDocumentAsync(processRequest);
        Assert.Equal(ContractOutcome.Failed, failed.Outcome);
        Assert.True(failed.Error?.Retryable);
        var failedRecord = await harness.Repository.GetAsync(
            D12TestContext.PreviousProcessingId,
            CancellationToken.None);
        harness.Reader.Seed(D12TestContext.Version1);

        var reprocessed = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());
        Assert.Equal(ContractOutcome.Success, reprocessed.Outcome);
        var captured = (await harness.Repository.GetAsync(
            D12TestContext.NewProcessingId,
            CancellationToken.None))!.PredecessorSnapshot;

        var retried = await harness.Application.ProcessDocumentAsync(processRequest);
        Assert.Equal(ContractOutcome.Success, retried.Outcome);
        Assert.Equal(ProcessingState.Completed, retried.Data?.State);
        Assert.Equal(ProcessingState.Failed, captured?.State);
        Assert.Equal(failedRecord?.TerminalEventId, captured?.TerminalEventId);
        Assert.Equal(3, harness.Repository.EventHistorySnapshot().Count);
    }

    [Fact]
    public async Task UpdatedEvidenceUsesExactDifferentImmutableVersionOfSameEvidence()
    {
        var harness = new D12Harness();
        await harness.CreateCompletedPredecessorAsync();
        harness.Reader.Seed(D12TestContext.Version2);

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
            versionId: D12TestContext.Version2,
            reason: ReprocessingReason.UpdatedEvidence));

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        var record = await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None);
        Assert.Equal(D12TestContext.Version2, record?.DocumentVersionId);
        Assert.All(record!.Facts, fact =>
        {
            Assert.Equal(D12TestContext.EvidenceId, fact.EvidenceId);
            Assert.Equal(D12TestContext.Version2, fact.DocumentVersionId);
        });
    }

    [Fact]
    public async Task UpdatedEvidenceRejectsSameVersionUnrelatedEvidenceAndCrossCustomerSource()
    {
        var sameVersion = new D12Harness();
        await sameVersion.CreateCompletedPredecessorAsync();
        Assert.Equal(
            ContractErrorCategory.PreconditionFailed,
            (await sameVersion.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
                reason: ReprocessingReason.UpdatedEvidence))).Error?.Category);

        var unrelated = new D12Harness();
        await unrelated.CreateCompletedPredecessorAsync();
        unrelated.Reader.Seed(D12TestContext.Version2, evidenceId: "unrelated-evidence");
        Assert.Equal(
            ContractErrorCategory.PreconditionFailed,
            (await unrelated.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
                versionId: D12TestContext.Version2,
                reason: ReprocessingReason.UpdatedEvidence))).Error?.Category);

        var crossCustomer = new D12Harness();
        await crossCustomer.CreateCompletedPredecessorAsync();
        crossCustomer.Reader.Seed(D12TestContext.Version2, D12TestContext.OtherCustomerId);
        Assert.Equal(
            ContractErrorCategory.PreconditionFailed,
            (await crossCustomer.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
                versionId: D12TestContext.Version2,
                reason: ReprocessingReason.UpdatedEvidence))).Error?.Category);
    }

    [Fact]
    public async Task ConcurrentIdenticalDeliveryBeforeAndAfterTerminalCompletionDoesNotExtractOrEmitTwice()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var extractionBaseline = harness.Extractor.Calls;
        var readBaseline = harness.Reader.Calls;
        var preflightArrivals = 0;
        var releasePreflight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.BeforeReadAsync = async (call, cancellationToken) =>
        {
            if (call is > 0 && call <= readBaseline + 2)
            {
                if (Interlocked.Increment(ref preflightArrivals) == 2)
                {
                    releasePreflight.TrySetResult(true);
                }

                await releasePreflight.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        };
        var request = D12TestContext.ReprocessRequest();

        var first = harness.Application.ReprocessDocumentAsync(request);
        var second = harness.Application.ReprocessDocumentAsync(request);
        var concurrent = await Task.WhenAll(first, second);
        harness.Reader.BeforeReadAsync = null;
        var replay = await harness.Application.ReprocessDocumentAsync(request with { RequestId = "lost-response-replay" });

        Assert.All(concurrent, result => Assert.Equal(ContractOutcome.Success, result.Outcome));
        Assert.Equal(ContractOutcome.Success, replay.Outcome);
        Assert.Equal(extractionBaseline + 1, harness.Extractor.Calls);
        Assert.Single(harness.Repository.OutboxSnapshot());
        Assert.True(harness.Policy.Calls >= 5);
    }

    [Fact]
    public async Task TerminalFailureReplayDoesNotExtractOrEmitAnotherFailure()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var extractionBaseline = harness.Extractor.Calls;
        harness.Extractor.ThrowControlledFailure = true;
        var request = D12TestContext.ReprocessRequest();

        var first = await harness.Application.ReprocessDocumentAsync(request);
        var replay = await harness.Application.ReprocessDocumentAsync(request with { RequestId = "replay-after-failure" });

        Assert.Equal(ContractOutcome.Failed, first.Outcome);
        Assert.Equal(ContractOutcome.Success, replay.Outcome);
        Assert.Equal(ProcessingState.Failed, replay.Data?.State);
        Assert.Single(harness.Repository.OutboxSnapshot());
        Assert.Equal(extractionBaseline + 1, harness.Extractor.Calls);
    }

    [Fact]
    public async Task SemanticPayloadAndOperationIdentityConflictsAreRejectedWhileCrossCustomerKeysAreScoped()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var request = D12TestContext.ReprocessRequest();
        Assert.Equal(ContractOutcome.Success, (await harness.Application.ReprocessDocumentAsync(request)).Outcome);

        var changedPayload = request with
        {
            Payload = request.Payload with { ProcessingId = "processing-changed" },
        };
        Assert.Equal(
            ContractErrorCategory.Conflict,
            (await harness.Application.ReprocessDocumentAsync(changedPayload)).Error?.Category);

        var secondKeySameOperation = request with { IdempotencyKey = "different-key" };
        Assert.Equal(
            ContractErrorCategory.Conflict,
            (await harness.Application.ReprocessDocumentAsync(secondKeySameOperation)).Error?.Category);

        await harness.CreateFailedPredecessorAsync("other-predecessor", customerId: D12TestContext.OtherCustomerId);
        var otherCustomer = D12TestContext.ReprocessRequest(
            processingId: "other-operation",
            previousProcessingId: "other-predecessor",
            customerId: D12TestContext.OtherCustomerId,
            idempotencyKey: request.IdempotencyKey!);
        Assert.Equal(
            ContractOutcome.Success,
            (await harness.Application.ReprocessDocumentAsync(otherCustomer)).Outcome);
    }

    [Fact]
    public async Task DistinctScopedKeyTuplesCannotCollideThroughDelimiterContent()
    {
        const string delimiter = "\u001f";
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync("predecessor-a", customerId: "customer-a");
        await harness.CreateFailedPredecessorAsync("predecessor-b", customerId: $"customer-a{delimiter}part");

        var first = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
            processingId: "operation-a",
            previousProcessingId: "predecessor-a",
            customerId: "customer-a",
            idempotencyKey: $"part{delimiter}key"));
        var second = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
            processingId: "operation-b",
            previousProcessingId: "predecessor-b",
            customerId: $"customer-a{delimiter}part",
            idempotencyKey: "key"));

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(ContractOutcome.Success, second.Outcome);
        Assert.NotEqual(first.Data?.ProcessingId, second.Data?.ProcessingId);
    }

    [Fact]
    public async Task ChangedPredecessorDuringEvidencePreflightIsRejectedAtAtomicAdmission()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();
        var readBaseline = harness.Reader.Calls;
        var preflightEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.BeforeReadAsync = async (call, cancellationToken) =>
        {
            if (call == readBaseline + 1)
            {
                preflightEntered.TrySetResult();
                await releasePreflight.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        };

        var reprocessing = harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());
        await preflightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var legacyRetry = await harness.Application.ProcessDocumentAsync(D12TestContext.ProcessRequest(
                D12TestContext.PreviousProcessingId,
                D12TestContext.Version1));
            Assert.Equal(ProcessingState.Completed, legacyRetry.Data?.State);
        }
        finally
        {
            releasePreflight.TrySetResult();
        }

        var result = await reprocessing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal(ContractErrorCategory.Conflict, result.Error?.Category);
        Assert.Null(await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None));
    }

    [Fact]
    public async Task CommittedFactsAndTerminalEventsAreImmutableOwnerSnapshots()
    {
        var harness = new D12Harness();
        var predecessor = await harness.CreateFailedPredecessorAsync();

        var result = await harness.Application.ReprocessDocumentAsync(D12TestContext.ReprocessRequest());
        Assert.Equal(ContractOutcome.Success, result.Outcome);
        var retainedFacts = Assert.IsType<List<ValidatedSourceFact>>(harness.Extractor.RetainedFacts);
        retainedFacts.Add(retainedFacts[0] with { SourceFactId = "mutated-extractor-alias" });

        var stored = Assert.IsType<Monergy.Services.DocumentIntelligence.Application.ProcessingRecord>(
            await harness.Repository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None));
        Assert.Single(stored.Facts);
        Assert.Equal(predecessor.TerminalEventId, stored.PredecessorSnapshot?.TerminalEventId);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ValidatedSourceFact>)stored.Facts).Add(retainedFacts[0]));

        var outboxSnapshot = harness.Repository.OutboxSnapshot();
        var produced = Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(Assert.Single(outboxSnapshot));
        Assert.Single(produced.Payload.Facts);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ValidatedSourceFact>)produced.Payload.Facts).Add(retainedFacts[0]));

        var history = harness.Repository.EventHistorySnapshot();
        var historyProduced = Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(history[^1]);
        Assert.Single(historyProduced.Payload.Facts);
        ((object[])outboxSnapshot)[0] = new object();
        Assert.IsType<DomainEvent<ValidatedSourceFactsProducedPayload>>(
            Assert.Single(harness.Repository.OutboxSnapshot()));
    }
}
