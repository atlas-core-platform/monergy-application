using System.Text.Json;
using Monergy.Contracts;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class ContractCompatibilityTests
{
    public static TheoryData<string, object, Type> ContractPayloads => new()
    {
        { "CID-020", Vs02TestContext.CreateVersionPayload(), typeof(CreateDocumentVersion) },
        { "CID-021", new GetEvidenceMetadata("document-001", Vs02TestContext.CustomerId), typeof(GetEvidenceMetadata) },
        { "CID-022", new GetEvidenceReference("document-version-001", Vs02TestContext.CustomerId), typeof(GetEvidenceReference) },
        { "CID-023", new EvidenceRegisteredPayload("evidence-001", "document-001", Vs02TestContext.CustomerId), typeof(EvidenceRegisteredPayload) },
        { "CID-024", new EvidenceVersionCreatedPayload("evidence-001", "document-001", "document-version-001", 1, Vs02TestContext.ContentSha256), typeof(EvidenceVersionCreatedPayload) },
        { "CID-025", new ProcessDocument("processing-001", "document-version-001", "evidence-001", Vs02TestContext.CustomerId, DateTimeOffset.UnixEpoch), typeof(ProcessDocument) },
        { "CID-027", new GetProcessingStatus("processing-001", Vs02TestContext.CustomerId), typeof(GetProcessingStatus) },
        { "CID-028", new ValidatedSourceFactsProducedPayload("processing-001", Vs02TestContext.CustomerId, "evidence-001", "document-version-001", []), typeof(ValidatedSourceFactsProducedPayload) },
        { "CID-031", new NormalizeSourceFacts("profile-001", Vs02TestContext.CustomerId, "processing-001", [], "normalization/1.0.0", DateTimeOffset.UnixEpoch), typeof(NormalizeSourceFacts) },
        { "CID-033", new GetFinancialProvenance("provenance-001", Vs02TestContext.CustomerId), typeof(GetFinancialProvenance) },
        { "CID-034", new FinancialFactChangedPayload("profile-001", "fact-001", "INCOME", DateTimeOffset.UnixEpoch, "provenance-001", 1), typeof(FinancialFactChangedPayload) },
        { "CID-035", new FinancialFactChangedPayload("profile-001", "fact-001", "INCOME", DateTimeOffset.UnixEpoch, "provenance-002", 2), typeof(FinancialFactChangedPayload) },
        { "CID-055", new ScheduleJob("job-001", Vs02ContractNames.ProcessDocument, "Document Intelligence Service", "processing-001", Vs02TestContext.CustomerId, DateTimeOffset.UnixEpoch), typeof(ScheduleJob) },
        { "CID-057", new GetJobStatus("job-001", Vs02TestContext.CustomerId), typeof(GetJobStatus) },
    };

    [Fact]
    public void CatalogPreservesTheExactFourteenContractIdentities()
    {
        var expected = new[]
        {
            "CID-020", "CID-021", "CID-022", "CID-023", "CID-024", "CID-025", "CID-027",
            "CID-028", "CID-031", "CID-033", "CID-034", "CID-035", "CID-055", "CID-057",
        };

        Assert.Equal(expected, Vs02ContractCatalog.All.Select(contract => contract.Id));
        Assert.Equal(14, Vs02ContractCatalog.All.Select(contract => contract.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Vs02ContractCatalog.All, contract => Assert.Equal(ContractGuard.CurrentVersion, "1.0.0"));
    }

    [Theory]
    [MemberData(nameof(ContractPayloads))]
    public void EveryContractPayloadRoundTripsWithStrictSerialization(string contractId, object payload, Type payloadType)
    {
        var serialized = JsonSerializer.Serialize(payload, payloadType, ContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize(serialized, payloadType, ContractJson.Options);

        Assert.NotNull(roundTrip);
        Assert.Contains(Vs02ContractCatalog.All, contract => contract.Id == contractId);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedVersionsAndMalformedSecurityContextAreRejected()
    {
        var request = Vs02TestContext.Request(
            Vs02ContractNames.GetEvidenceMetadata,
            new GetEvidenceMetadata("document-001", Vs02TestContext.CustomerId),
            "request-unsupported") with
        { ContractVersion = "2.0.0" };
        Assert.Equal(ContractErrorCategory.UnsupportedOperation, ContractGuard.Validate(request, Vs02ContractNames.GetEvidenceMetadata)?.Category);

        var missingActor = request with
        {
            ContractVersion = ContractGuard.CurrentVersion,
            Security = request.Security with { Actor = request.Security.Actor with { ActorId = string.Empty } },
        };
        Assert.Equal(ContractErrorCategory.AuthenticationRequired, ContractGuard.Validate(missingActor, Vs02ContractNames.GetEvidenceMetadata)?.Category);
    }
}
