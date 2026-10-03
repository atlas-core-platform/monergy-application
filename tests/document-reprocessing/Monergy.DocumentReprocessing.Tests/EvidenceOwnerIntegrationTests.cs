using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.DocumentReprocessing.Tests;

public sealed class EvidenceOwnerIntegrationTests
{
    [Fact]
    public async Task EvidenceOwnerContractsSupplyExactVersionForUpdatedEvidenceReprocessing()
    {
        var telemetry = new D12Telemetry();
        var clock = new D12TimeProvider(D12TestContext.Now);
        var content = new ReferenceEvidenceContentStore();
        var evidenceRepository = new InMemoryEvidenceRepository();
        var evidence = new EvidenceApplication(evidenceRepository, content, telemetry, clock);
        await CreateVersionAsync(evidence, content, D12TestContext.Version1, "reference://d12/version-1", "a");
        await CreateVersionAsync(evidence, content, D12TestContext.Version2, "reference://d12/version-2", "b");

        var metadata = await evidence.GetEvidenceMetadataAsync(D12TestContext.Request(
            Vs02ContractNames.GetEvidenceMetadata,
            new GetEvidenceMetadata(D12TestContext.DocumentId, D12TestContext.CustomerId),
            "unused",
            "metadata-d12"));
        Assert.Equal(2, metadata.Data?.Versions.Count);

        var processingRepository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(
            processingRepository,
            new EvidenceOwnerReader(evidence, content),
            new CountingDocumentExtractor(),
            new ScriptedExecutionPolicy(),
            telemetry,
            clock);
        var predecessor = await processing.ProcessDocumentAsync(D12TestContext.ProcessRequest(
            D12TestContext.PreviousProcessingId,
            D12TestContext.Version1));
        Assert.Equal(ContractOutcome.Success, predecessor.Outcome);

        var reprocessed = await processing.ReprocessDocumentAsync(D12TestContext.ReprocessRequest(
            versionId: D12TestContext.Version2,
            reason: ReprocessingReason.UpdatedEvidence));

        Assert.Equal(ContractOutcome.Success, reprocessed.Outcome);
        var record = await processingRepository.GetAsync(D12TestContext.NewProcessingId, CancellationToken.None);
        Assert.Equal(D12TestContext.Version2, record?.DocumentVersionId);
        Assert.All(record!.Facts, fact =>
        {
            Assert.Equal(D12TestContext.EvidenceId, fact.EvidenceId);
            Assert.Equal(D12TestContext.Version2, fact.DocumentVersionId);
        });
    }

    private static async Task CreateVersionAsync(
        EvidenceApplication evidence,
        ReferenceEvidenceContentStore content,
        string versionId,
        string contentReference,
        string hashCharacter)
    {
        var hash = new string(hashCharacter[0], 64);
        content.Seed(contentReference, hash, "text/plain", D12TestContext.ValidContent);
        var result = await evidence.CreateDocumentVersionAsync(D12TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            new CreateDocumentVersion(
                D12TestContext.DocumentId,
                versionId,
                D12TestContext.EvidenceId,
                D12TestContext.CustomerId,
                "MANUAL_UPLOAD",
                $"{versionId}.txt",
                "text/plain",
                contentReference,
                hash,
                D12TestContext.Now),
            $"evidence-key-{versionId}",
            $"create-{versionId}"));
        Assert.Equal(ContractOutcome.Success, result.Outcome);
    }

    private sealed class EvidenceOwnerReader(
        EvidenceApplication evidence,
        ReferenceEvidenceContentStore content) : IEvidenceContentReader
    {
        public async Task<EvidenceContent?> ReadAsync(
            string documentVersionId,
            string customerId,
            TrustedSecurityContext security,
            string correlationId,
            CancellationToken cancellationToken)
        {
            var request = D12TestContext.Request(
                Vs02ContractNames.GetEvidenceReference,
                new GetEvidenceReference(documentVersionId, customerId),
                "unused",
                $"reference-{documentVersionId}",
                security) with
            {
                CorrelationId = correlationId,
            };
            var wireRequest = JsonSerializer.Deserialize<ContractRequest<GetEvidenceReference>>(
                JsonSerializer.Serialize(request, ContractJson.Options),
                ContractJson.Options)!;
            var result = await evidence.GetEvidenceReferenceAsync(wireRequest, cancellationToken);
            var wireResult = JsonSerializer.Deserialize<ContractResult<EvidenceReference>>(
                JsonSerializer.Serialize(result, ContractJson.Options),
                ContractJson.Options)!;
            if (wireResult.Outcome != ContractOutcome.Success || wireResult.Data is null)
            {
                return null;
            }

            var value = await content.ReadAsync(wireResult.Data.ContentReference, cancellationToken);
            return value is null ? null : new EvidenceContent(wireResult.Data, value);
        }
    }
}
