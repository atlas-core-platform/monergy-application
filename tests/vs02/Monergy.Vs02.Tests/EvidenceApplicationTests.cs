using Monergy.Contracts;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class EvidenceApplicationTests
{
    [Fact]
    public async Task CreatesImmutableVersionAndReplaysIdempotently()
    {
        var repository = new InMemoryEvidenceRepository();
        var content = new ReferenceEvidenceContentStore();
        content.Seed(Vs02TestContext.ContentReference, Vs02TestContext.ContentSha256, "text/plain", Vs02TestContext.Fixture);
        var app = new EvidenceApplication(repository, content, new CapturingTelemetry(), TimeProvider.System);
        var request = Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload(),
            "request-version-001",
            "idempotency-version-001");

        var first = await app.CreateDocumentVersionAsync(request);
        var replay = await app.CreateDocumentVersionAsync(request);
        var metadata = await app.GetEvidenceMetadataAsync(Vs02TestContext.Request(
            Vs02ContractNames.GetEvidenceMetadata,
            new GetEvidenceMetadata("document-001", Vs02TestContext.CustomerId),
            "request-metadata-001"));

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(first.Data, replay.Data);
        Assert.Single(metadata.Data!.Versions);
        Assert.Equal(2, repository.DrainOutbox().Count);
    }

    [Fact]
    public async Task RejectsUnsafeFileNameUnverifiedContentAndCrossCustomerAccess()
    {
        var repository = new InMemoryEvidenceRepository();
        var content = new ReferenceEvidenceContentStore();
        content.Seed(Vs02TestContext.ContentReference, Vs02TestContext.ContentSha256, "text/plain", Vs02TestContext.Fixture);
        var app = new EvidenceApplication(repository, content, new CapturingTelemetry(), TimeProvider.System);
        var unsafePayload = Vs02TestContext.CreateVersionPayload() with { OriginalFileName = "../statement.txt" };
        var unsafeResult = await app.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            unsafePayload,
            "request-unsafe",
            "idempotency-unsafe"));
        Assert.Equal(ContractErrorCategory.ValidationError, unsafeResult.Error?.Category);

        var unverified = await app.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload() with { ContentSha256 = new string('b', 64) },
            "request-unverified",
            "idempotency-unverified"));
        Assert.Equal(ContractErrorCategory.PreconditionFailed, unverified.Error?.Category);

        await app.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload(),
            "request-valid",
            "idempotency-valid"));
        var denied = await app.GetEvidenceReferenceAsync(Vs02TestContext.Request(
            Vs02ContractNames.GetEvidenceReference,
            new GetEvidenceReference("document-version-001", "customer-002"),
            "request-denied",
            security: Vs02TestContext.Security("customer-002")));
        Assert.Equal(ContractErrorCategory.NotFound, denied.Error?.Category);
    }
}
