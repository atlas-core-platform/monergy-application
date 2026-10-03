using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;

namespace Monergy.DocumentReprocessing.Tests;

internal sealed class D12TimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now += duration;
}

internal sealed class D12Telemetry : ILifecycleTelemetry
{
    public List<LifecycleSignal> Signals { get; } = [];
    public void Record(LifecycleSignal signal) => Signals.Add(signal);
}

internal sealed class ScriptedExecutionPolicy : IExecutionPolicy
{
    private readonly object sync = new();
    private readonly Queue<ContractError?> outcomes = [];
    private int calls;
    public int Calls => Volatile.Read(ref calls);

    public void Enqueue(ContractError? outcome)
    {
        lock (sync) { outcomes.Enqueue(outcome); }
    }

    public Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext security,
        string customerId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref calls);
        lock (sync)
        {
            return Task.FromResult(outcomes.Count == 0 ? null : outcomes.Dequeue());
        }
    }
}

internal sealed class ScriptedEvidenceReader : IEvidenceContentReader
{
    private readonly object sync = new();
    private readonly Dictionary<(string CustomerId, string VersionId), EvidenceContent> sources = [];
    private readonly Queue<Func<string, string, EvidenceContent?>> outcomes = [];
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public Func<int, CancellationToken, Task>? BeforeReadAsync { get; set; }

    public void Seed(
        string versionId,
        string customerId = D12TestContext.CustomerId,
        string evidenceId = D12TestContext.EvidenceId,
        string content = D12TestContext.ValidContent)
    {
        lock (sync)
        {
            sources[(customerId, versionId)] = new EvidenceContent(
                new EvidenceReference(
                    evidenceId,
                    D12TestContext.DocumentId,
                    versionId,
                    customerId,
                    $"reference://{customerId}/{versionId}",
                    new string('a', 64),
                    "text/plain"),
                content);
        }
    }

    public void Enqueue(EvidenceContent? outcome)
    {
        lock (sync) { outcomes.Enqueue((_, _) => outcome); }
    }

    public void EnqueueDependencyFailure()
    {
        lock (sync)
        {
            outcomes.Enqueue((_, _) =>
                throw new EvidenceDependencyException("controlled fixture dependency failure"));
        }
    }

    public async Task<EvidenceContent?> ReadAsync(
        string documentVersionId,
        string customerId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref calls);
        if (BeforeReadAsync is not null)
        {
            await BeforeReadAsync(call, cancellationToken);
        }

        lock (sync)
        {
            return outcomes.Count > 0
                ? outcomes.Dequeue()(documentVersionId, customerId)
                : sources.GetValueOrDefault((customerId, documentVersionId));
        }
    }

    public EvidenceContent Required(string versionId, string customerId = D12TestContext.CustomerId)
    {
        lock (sync) { return sources[(customerId, versionId)]; }
    }
}

internal sealed class CountingDocumentExtractor : IDocumentExtractor
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public bool ThrowControlledFailure { get; set; }
    public Action<CancellationToken>? BeforeExtract { get; set; }
    public List<ValidatedSourceFact>? RetainedFacts { get; private set; }

    public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(
        EvidenceContent content,
        string processingId,
        CancellationToken cancellationToken)
    {
        BeforeExtract?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref calls);
        if (ThrowControlledFailure)
        {
            throw new DocumentExtractionException("controlled fixture extraction failure");
        }

        if (content.Content == D12TestContext.EmptyContent)
        {
            return Task.FromResult<IReadOnlyList<ValidatedSourceFact>>([]);
        }

        RetainedFacts =
        [
            new(
                $"fact-{processingId}",
                content.Reference.CustomerId,
                "INCOME",
                "Salary",
                125000m,
                "INR",
                new DateOnly(2026, 10, 1),
                "page:1",
                0.98m,
                content.Reference.EvidenceId,
                content.Reference.DocumentVersionId,
                FixtureDocumentExtractor.ExtractionVersion,
                FixtureDocumentExtractor.ValidationVersion),
        ];
        return Task.FromResult<IReadOnlyList<ValidatedSourceFact>>(RetainedFacts);
    }
}

internal sealed class D12Harness
{
    public InMemoryDocumentProcessingRepository Repository { get; } = new();
    public ScriptedEvidenceReader Reader { get; } = new();
    public CountingDocumentExtractor Extractor { get; } = new();
    public ScriptedExecutionPolicy Policy { get; } = new();
    public D12Telemetry Telemetry { get; } = new();
    public D12TimeProvider Clock { get; } = new(D12TestContext.Now);
    public DocumentProcessingApplication Application { get; }

    public D12Harness()
    {
        Application = new(Repository, Reader, Extractor, Policy, Telemetry, Clock);
    }

    public async Task<ProcessingRecord> CreateFailedPredecessorAsync(
        string processingId = D12TestContext.PreviousProcessingId,
        string versionId = D12TestContext.Version1,
        string customerId = D12TestContext.CustomerId)
    {
        Reader.Seed(versionId, customerId);
        Reader.EnqueueDependencyFailure();
        var result = await Application.ProcessDocumentAsync(D12TestContext.ProcessRequest(processingId, versionId, customerId));
        if (result.Outcome != ContractOutcome.Failed)
        {
            throw new InvalidOperationException("Failed predecessor fixture was not created.");
        }
        Repository.DrainOutbox();
        return (await Repository.GetAsync(processingId, CancellationToken.None))!;
    }

    public async Task<ProcessingRecord> CreateCompletedPredecessorAsync(
        string processingId = D12TestContext.PreviousProcessingId,
        string versionId = D12TestContext.Version1,
        string customerId = D12TestContext.CustomerId)
    {
        Reader.Seed(versionId, customerId);
        var result = await Application.ProcessDocumentAsync(D12TestContext.ProcessRequest(processingId, versionId, customerId));
        if (result.Outcome != ContractOutcome.Success)
        {
            throw new InvalidOperationException("Completed predecessor fixture was not created.");
        }

        Repository.DrainOutbox();
        return (await Repository.GetAsync(processingId, CancellationToken.None))!;
    }
}

internal static class D12TestContext
{
    public const string CustomerId = "customer-d12";
    public const string OtherCustomerId = "customer-other";
    public const string DocumentId = "document-d12";
    public const string EvidenceId = "evidence-d12";
    public const string Version1 = "document-version-d12-1";
    public const string Version2 = "document-version-d12-2";
    public const string PreviousProcessingId = "processing-d12-previous";
    public const string NewProcessingId = "processing-d12-reprocess";
    public const string ValidContent = "INCOME|Salary|125000.00|INR|2026-10-01|page:1|0.98";
    public const string EmptyContent = "NO_VALID_FACTS";
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    public static TrustedSecurityContext Security(string customerId = CustomerId) => new(
        new("actor-d12", "CUSTOMER", Now, "authentication-d12"),
        new("document-intelligence", "workload-d12"),
        new("DOCUMENT_REPROCESSING", "consent-d12", "authorization-d12", customerId));

    public static ContractRequest<T> Request<T>(
        string name,
        T payload,
        string idempotencyKey,
        string requestId = "request-d12",
        TrustedSecurityContext? security = null) => new(
        name,
        ContractGuard.CurrentVersion,
        requestId,
        "correlation-d12",
        "causation-d12",
        security ?? Security(),
        idempotencyKey,
        payload);

    public static ContractRequest<ProcessDocument> ProcessRequest(
        string processingId,
        string versionId,
        string customerId = CustomerId,
        string? idempotencyKey = null) => Request(
        Vs02ContractNames.ProcessDocument,
        new ProcessDocument(processingId, versionId, EvidenceId, customerId, Now),
        idempotencyKey ?? $"key-{processingId}",
        $"request-{processingId}",
        Security(customerId));

    public static ContractRequest<ReprocessDocument> ReprocessRequest(
        string processingId = NewProcessingId,
        string previousProcessingId = PreviousProcessingId,
        string versionId = Version1,
        ReprocessingReason reason = ReprocessingReason.FailedProcessing,
        string customerId = CustomerId,
        string evidenceId = EvidenceId,
        string idempotencyKey = "key-reprocess-d12",
        TrustedSecurityContext? security = null) => Request(
        D12ContractNames.ReprocessDocument,
        new ReprocessDocument(
            processingId,
            previousProcessingId,
            customerId,
            evidenceId,
            versionId,
            reason,
            Now),
        idempotencyKey,
        $"request-{processingId}",
        security ?? Security(customerId));
}
