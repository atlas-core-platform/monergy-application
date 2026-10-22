using Monergy.Contracts;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class EvidenceApplicationTests
{
    [Fact]
    public async Task LibraryPagesUseStableOwnerScopedVersionCursor()
    {
        var repository = new InMemoryEvidenceRepository();
        var app = new EvidenceApplication(repository, new ReferenceEvidenceContentStore(), new CapturingTelemetry(), TimeProvider.System);
        for (var i = 0; i < 205; i++)
        {
            var id = $"version-{i:000}";
            var at = DateTimeOffset.UnixEpoch;
            var version = new EvidenceVersionRecord("document-" + id, id, "evidence-" + id, Vs02TestContext.CustomerId,
                1, "Upload", "statement.txt", "text/plain", "reference://content", new string('a', 64), at, at);
            await repository.SaveVersionAsync(new("create", "1.0", Vs02TestContext.CustomerId, id), version, _ => [], CancellationToken.None);
        }
        async Task<ContractResult<CustomerEvidenceLibrary>> Page(string? after) => await app.ListCustomerEvidenceAsync(Vs02TestContext.Request(
            App02ContractNames.ListCustomerEvidence, new ListCustomerEvidence(Vs02TestContext.CustomerId, 100, after), "page-request"));
        var first = (await Page(null)).Data!;
        var second = (await Page(first.NextVersionId)).Data!;
        var third = (await Page(second.NextVersionId)).Data!;
        Assert.Equal(100, first.Items.Count); Assert.Equal(100, second.Items.Count); Assert.Equal(5, third.Items.Count);
        Assert.Null(third.NextVersionId);
        Assert.Equal(205, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.DocumentVersionId).Distinct().Count());
        Assert.Equal("version-204", first.Items[0].DocumentVersionId);
        Assert.Equal("version-000", third.Items[^1].DocumentVersionId);
        Assert.Equal(ContractOutcome.Rejected, (await Page("missing-version")).Outcome);
        Assert.Equal(ContractOutcome.Rejected, (await Page("../invalid")).Outcome);
        var wrongCustomer = await app.ListCustomerEvidenceAsync(Vs02TestContext.Request(App02ContractNames.ListCustomerEvidence,
            new ListCustomerEvidence("customer-002", 100, first.NextVersionId), "other-page", security: Vs02TestContext.Security("customer-002")));
        Assert.Equal("evidence.library.cursor-invalid", wrongCustomer.Error!.Code);
    }

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

    [Fact]
    public async Task ListsOnlyCurrentCustomerEvidenceWithBoundedInput()
    {
        var repository = new InMemoryEvidenceRepository();
        var content = new ReferenceEvidenceContentStore();
        content.Seed(Vs02TestContext.ContentReference, Vs02TestContext.ContentSha256, "text/plain", Vs02TestContext.Fixture);
        var telemetry = new CapturingTelemetry();
        var app = new EvidenceApplication(repository, content, telemetry, TimeProvider.System);
        await app.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion,
            Vs02TestContext.CreateVersionPayload(),
            "request-library-create",
            "idempotency-library-create"));

        var listed = await app.ListCustomerEvidenceAsync(Vs02TestContext.Request(
            App02ContractNames.ListCustomerEvidence,
            new ListCustomerEvidence(Vs02TestContext.CustomerId, 25),
            "request-library-list"));
        var denied = await app.ListCustomerEvidenceAsync(Vs02TestContext.Request(
            App02ContractNames.ListCustomerEvidence,
            new ListCustomerEvidence("customer-002", 25),
            "request-library-denied"));
        var unbounded = await app.ListCustomerEvidenceAsync(Vs02TestContext.Request(
            App02ContractNames.ListCustomerEvidence,
            new ListCustomerEvidence(Vs02TestContext.CustomerId, 101),
            "request-library-unbounded"));

        Assert.Equal(ContractOutcome.Success, listed.Outcome);
        var item = Assert.Single(listed.Data!.Items);
        Assert.Equal("document-version-001", item.DocumentVersionId);
        Assert.Equal("financial-statement.txt", item.OriginalFileName);
        Assert.Equal(Vs02TestContext.ContentSha256, item.ContentSha256);
        Assert.DoesNotContain(item.GetType().GetProperties(), property => property.Name == "ContentReference");
        Assert.Equal(ContractErrorCategory.AccessDenied, denied.Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError, unbounded.Error?.Category);
        Assert.Contains(telemetry.Signals, signal =>
            signal.Operation == App02ContractNames.ListCustomerEvidence && signal.Outcome == "LISTED");
    }
}
