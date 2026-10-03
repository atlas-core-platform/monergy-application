using System.Text.Json;
using System.Text.Json.Nodes;
using Monergy.Contracts;
using Xunit;

namespace Monergy.DocumentReprocessing.Tests;

public sealed class ContractAndSchemaTests
{
    private static readonly string[] SecurityMemberNames = ["actor", "workload", "access"];

    [Fact]
    public void D12CatalogHasExactTwelveFamilyScopeAndTwoNewRealizations()
    {
        var expected = new[]
        {
            "CID-007", "CID-020", "CID-021", "CID-022", "CID-025", "CID-026",
            "CID-027", "CID-028", "CID-029", "CID-055", "CID-057", "CID-061",
        };

        Assert.Equal(expected, D12ContractCatalog.Contracts.Select(item => item.ContractId));
        Assert.Equal(
            ["CID-026", "CID-029"],
            D12ContractCatalog.Contracts
                .Where(item => item.Treatment == "NEWLY_REALIZED")
                .Select(item => item.ContractId));
        Assert.Equal(14, Vs02ContractCatalog.All.Count);
        Assert.DoesNotContain(Vs02ContractCatalog.All, item => item.Id is "CID-026" or "CID-029");
    }

    [Fact]
    public void ReprocessingRequestAndFailureEventRoundTripStrictly()
    {
        var request = D12TestContext.ReprocessRequest();
        var requestJson = JsonSerializer.Serialize(request, ContractJson.Options);
        Assert.Contains("\"reason\":\"FAILED_PROCESSING\"", requestJson, StringComparison.Ordinal);
        var requestRoundTrip = JsonSerializer.Deserialize<ContractRequest<ReprocessDocument>>(requestJson, ContractJson.Options);
        Assert.Equal(request, requestRoundTrip);

        var failure = new DomainEvent<DocumentProcessingFailedPayload>(
            "CID-029",
            "event-d12",
            D12ContractNames.DocumentProcessingFailed,
            ContractGuard.CurrentVersion,
            D12TestContext.Now,
            "correlation-d12",
            "request-d12",
            "Document Intelligence Service",
            "document-processing",
            D12TestContext.NewProcessingId,
            new(
                D12TestContext.NewProcessingId,
                D12TestContext.CustomerId,
                D12TestContext.EvidenceId,
                D12TestContext.Version1,
                "processing.no-valid-facts",
                ContractErrorCategory.ProcessingFailed,
                false,
                D12TestContext.PreviousProcessingId,
                "processing-d12-reprocess:attempt:1"));
        var eventJson = JsonSerializer.Serialize(failure, ContractJson.Options);
        Assert.Equal(
            failure,
            JsonSerializer.Deserialize<DomainEvent<DocumentProcessingFailedPayload>>(eventJson, ContractJson.Options));

        var malformed = requestJson.Insert(requestJson.Length - 1, ",\"unexpected\":true");
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ContractRequest<ReprocessDocument>>(malformed, ContractJson.Options));
    }

    [Fact]
    public async Task WrongContractVersionNullPayloadContextAndUnsupportedReasonFailSafely()
    {
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();

        var wrongVersion = D12TestContext.ReprocessRequest() with { ContractVersion = "2.0.0" };
        Assert.Equal(
            ContractErrorCategory.UnsupportedOperation,
            (await harness.Application.ReprocessDocumentAsync(wrongVersion)).Error?.Category);

        var wrongName = D12TestContext.ReprocessRequest() with { ContractName = "RetryDocument" };
        Assert.Equal(
            ContractErrorCategory.UnsupportedOperation,
            (await harness.Application.ReprocessDocumentAsync(wrongName)).Error?.Category);

        var nullPayload = D12TestContext.ReprocessRequest() with { Payload = null! };
        Assert.Equal(
            ContractErrorCategory.ValidationError,
            (await harness.Application.ReprocessDocumentAsync(nullPayload)).Error?.Category);

        var nullContext = D12TestContext.ReprocessRequest() with { Security = null! };
        Assert.Equal(
            ContractErrorCategory.AuthenticationRequired,
            (await harness.Application.ReprocessDocumentAsync(nullContext)).Error?.Category);

        var unsupportedReason = D12TestContext.ReprocessRequest() with
        {
            Payload = D12TestContext.ReprocessRequest().Payload with { Reason = (ReprocessingReason)999 },
        };
        Assert.Equal(
            ContractErrorCategory.ValidationError,
            (await harness.Application.ReprocessDocumentAsync(unsupportedReason)).Error?.Category);
    }

    [Fact]
    public void D12SchemaIsClosedAndContainsOnlyCid026AndCid029()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "contracts", "schemas", "document-reprocessing.schema.json")));
        var root = schema.RootElement;
        Assert.Equal(2, root.GetProperty("oneOf").GetArrayLength());
        Assert.False(root.GetProperty("$defs").GetProperty("reprocessDocument").GetProperty("additionalProperties").GetBoolean());
        Assert.False(root.GetProperty("$defs").GetProperty("documentProcessingFailed").GetProperty("additionalProperties").GetBoolean());
        var text = root.ToString();
        Assert.Contains("ReprocessDocument", text, StringComparison.Ordinal);
        Assert.Contains("DocumentProcessingFailed", text, StringComparison.Ordinal);
        Assert.Contains("CID-029", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CID-030", text, StringComparison.Ordinal);
        var security = root.GetProperty("$defs").GetProperty("security");
        Assert.False(security.GetProperty("additionalProperties").GetBoolean());
        Assert.All(SecurityMemberNames, name =>
        {
            var nested = security.GetProperty("properties").GetProperty(name);
            Assert.False(nested.GetProperty("additionalProperties").GetBoolean());
            Assert.NotEmpty(nested.GetProperty("required").EnumerateArray());
        });
        Assert.Contains(
            "ProcessingFailed",
            root.GetProperty("$defs").GetProperty("documentProcessingFailed")
                .GetProperty("properties").GetProperty("payload")
                .GetProperty("properties").GetProperty("errorCategory")
                .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task SerializedCid026WireRejectsMissingNumericUnsupportedAndMalformedMembers()
    {
        var validJson = JsonSerializer.Serialize(D12TestContext.ReprocessRequest(), ContractJson.Options);
        var harness = new D12Harness();
        await harness.CreateFailedPredecessorAsync();

        Assert.Throws<JsonException>(() => DeserializeRequest(Mutate(validJson, root =>
            root["payload"]!.AsObject().Remove("reason"))));
        Assert.Throws<JsonException>(() => DeserializeRequest(Mutate(validJson, root =>
            root["payload"]!["reason"] = 999)));
        Assert.Throws<JsonException>(() => DeserializeRequest(Mutate(validJson, root =>
            root["payload"]!["reason"] = "INVENTED_REASON")));
        Assert.Throws<JsonException>(() => DeserializeRequest(Mutate(validJson, root =>
            root["security"]!["actor"]!["unexpected"] = true)));

        var missingKey = DeserializeRequest(Mutate(validJson, root => root.Remove("idempotencyKey")));
        var missingIdentity = DeserializeRequest(Mutate(validJson, root =>
            root["payload"]!.AsObject().Remove("processingId")));
        var nullSecurity = DeserializeRequest(Mutate(validJson, root => root["security"] = null));
        var nullPayload = DeserializeRequest(Mutate(validJson, root => root["payload"] = null));
        var malformedSecurity = DeserializeRequest(Mutate(validJson, root =>
        {
            root["security"]!["actor"] = new JsonObject();
            root["security"]!["workload"] = new JsonObject();
            root["security"]!["access"] = new JsonObject();
        }));

        Assert.Equal(ContractErrorCategory.ValidationError,
            (await harness.Application.ReprocessDocumentAsync(missingKey)).Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError,
            (await harness.Application.ReprocessDocumentAsync(missingIdentity)).Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.Application.ReprocessDocumentAsync(nullSecurity)).Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError,
            (await harness.Application.ReprocessDocumentAsync(nullPayload)).Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.Application.ReprocessDocumentAsync(malformedSecurity)).Error?.Category);
    }

    [Fact]
    public void SerializedCid029WireRejectsInventedOrNumericFailureCategories()
    {
        var failure = FailureEvent();
        var validJson = JsonSerializer.Serialize(failure, ContractJson.Options);
        Assert.Equal(failure, JsonSerializer.Deserialize<DomainEvent<DocumentProcessingFailedPayload>>(
            validJson, ContractJson.Options));
        Assert.Throws<JsonException>(() => DeserializeFailure(Mutate(validJson, root =>
            root["payload"]!["errorCategory"] = "InventedFailure")));
        Assert.Throws<JsonException>(() => DeserializeFailure(Mutate(validJson, root =>
            root["payload"]!["errorCategory"] = 999)));
        Assert.Throws<JsonException>(() => DeserializeFailure(Mutate(validJson, root =>
            root["payload"]!.AsObject().Remove("errorCategory"))));
    }

    [Fact]
    public void MachineReadableSchemaFixturesMatchActualDotNetSerialization()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "tests", "document-reprocessing", "Monergy.DocumentReprocessing.Tests", "WireFixtures.json")));
        var cases = fixtures.RootElement.GetProperty("fixtures").EnumerateArray().ToDictionary(
            fixture => fixture.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var requestJson = JsonSerializer.SerializeToNode(D12TestContext.ReprocessRequest(), ContractJson.Options);
        var eventJson = JsonSerializer.SerializeToNode(FailureEvent(), ContractJson.Options);

        Assert.True(JsonNode.DeepEquals(requestJson, JsonNode.Parse(cases["cid026-valid"].GetProperty("instance").GetRawText())));
        Assert.True(JsonNode.DeepEquals(eventJson, JsonNode.Parse(cases["cid029-valid"].GetProperty("instance").GetRawText())));
        Assert.All(cases.Values, fixture =>
            Assert.True(fixture.GetProperty("expectedSchemaOutcome").GetString() is "VALID" or "INVALID"));
    }

    private static ContractRequest<ReprocessDocument> DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<ContractRequest<ReprocessDocument>>(json, ContractJson.Options)
        ?? throw new JsonException("The CID-026 request was null.");

    private static DomainEvent<DocumentProcessingFailedPayload> DeserializeFailure(string json) =>
        JsonSerializer.Deserialize<DomainEvent<DocumentProcessingFailedPayload>>(json, ContractJson.Options)
        ?? throw new JsonException("The CID-029 event was null.");

    private static string Mutate(string json, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("The fixture was not an object.");
        mutation(root);
        return root.ToJsonString(ContractJson.Options);
    }

    private static DomainEvent<DocumentProcessingFailedPayload> FailureEvent() => new(
        "CID-029",
        "event-d12",
        D12ContractNames.DocumentProcessingFailed,
        ContractGuard.CurrentVersion,
        D12TestContext.Now,
        "correlation-d12",
        "request-d12",
        "Document Intelligence Service",
        "document-processing",
        D12TestContext.NewProcessingId,
        new(
            D12TestContext.NewProcessingId,
            D12TestContext.CustomerId,
            D12TestContext.EvidenceId,
            D12TestContext.Version1,
            "processing.no-valid-facts",
            ContractErrorCategory.ProcessingFailed,
            false,
            D12TestContext.PreviousProcessingId,
            "processing-d12-reprocess:attempt:1"));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "repository.manifest.json")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
