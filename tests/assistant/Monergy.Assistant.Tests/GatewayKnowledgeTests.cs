using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Monergy.Platform;
using Monergy.UatGateway;
using Xunit;

namespace Monergy.Assistant.Tests;

public sealed class GatewayKnowledgeTests
{
    private const string Question = "What does Monergy know about this product feature?";
    private static readonly string[] RequestFields = ["contractVersion", "question"];
    private static string CataloguePath => Path.Combine(Environment.GetEnvironmentVariable("MONERGY_ASSISTANT_EXPERT_ROOT")
        ?? throw new InvalidOperationException("An exact Expert candidate fixture is required."), "generated", "assistant-runtime", "validation-catalogue.json");

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Monergy:Assistant:CataloguePath"] = CataloguePath,
        ["Monergy:Assistant:CatalogueSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(CataloguePath))),
        ["Monergy:Assistant:ProductToken"] = new string('a', 64), ["Monergy:Assistant:PlatformToken"] = new string('b', 64),
    }).Build();

    private static JsonObject Answer(string audience, string state = "ANSWERED", string? claimId = null)
    {
        using var catalogue = new AssistantKnowledgeCatalogue(CataloguePath, Configuration()["Monergy:Assistant:CatalogueSha256"]!);
        var claims = catalogue.Scope(audience).GetProperty("claims").EnumerateArray();
        var claim = claimId is null ? claims.First(c => c.GetProperty("knowledgeStatus").GetString() is "AUTHORITATIVE" or "CURATED" or "DERIVED") :
            claims.Single(c => c.GetProperty("claimId").GetString() == claimId);
        return new JsonObject
        {
            ["contractVersion"] = "1.0.0", ["answerId"] = Guid.NewGuid().ToString(), ["state"] = state, ["question"] = Question,
            ["answer"] = state == "ANSWERED" ? claim.GetProperty("text").GetString() : "This decision remains unresolved.",
            ["scope"] = audience == "platform" ? "PLATFORM_KNOWLEDGE" : "PRODUCT_KNOWLEDGE",
            ["claims"] = state == "ANSWERED" ? new JsonArray(JsonNode.Parse(claim.GetRawText())) : new JsonArray(),
            ["limitations"] = new JsonArray(), ["nextActions"] = new JsonArray(),
            ["knowledgeRevision"] = catalogue.KnowledgeRevision, ["sourceRevision"] = JsonNode.Parse(catalogue.SourceRevision.GetRawText()),
            ["modelProcessing"] = new JsonObject { ["state"] = "NOT_USED" }, ["recordedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private sealed class RuntimeTransport(JsonObject value, Action<HttpRequestMessage>? observe = null, string? badHeader = null,
        HttpStatusCode status = HttpStatusCode.OK, string? raw = null, Action<HttpResponseMessage>? mutate = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observe?.Invoke(request);
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                Assert.Equal(RequestFields, body.RootElement.EnumerateObject().Select(p => p.Name));
                Assert.Equal(Question, body.RootElement.GetProperty("question").GetString());
            }
            var response = new HttpResponseMessage(status) { Content = new StringContent(raw ?? value.ToJsonString(), Encoding.UTF8, "application/json") };
            using var catalogue = new AssistantKnowledgeCatalogue(CataloguePath, Configuration()["Monergy:Assistant:CatalogueSha256"]!);
            response.Headers.Add("X-Monergy-Knowledge-Catalogue", badHeader == "catalogue" ? new string('0', 64) : catalogue.Digest);
            response.Headers.Add("X-Monergy-Knowledge-Retrieval", badHeader == "retrieval" ? new string('0', 64) : catalogue.RetrievalRevision);
            response.Headers.Add("X-Monergy-Knowledge-Audience", badHeader == "audience" ? new string('0', 64) : catalogue.AudienceRevision);
            mutate?.Invoke(response);
            return response;
        }
    }

    [Fact]
    public async Task UnresolvedExactCanonicalClaimCannotBePresentedAsAnswered()
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer("platform", claimId: "decision.od-11.claim")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnswerAsync("platform", new("1.0.0", Question), CancellationToken.None));
    }

    [Fact]
    public async Task PlatformUnresolvedDiagnosticWithoutClaimsRemainsValid()
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer("platform", "UNRESOLVED")));
        var answer = await client.AnswerAsync("platform", new("1.0.0", Question), CancellationToken.None);
        Assert.Equal("UNRESOLVED", answer.Projection.State); Assert.Empty(answer.Projection.Sources);
    }

    [Theory]
    [InlineData("product", "internal/v1/assistant/answers", 'a')]
    [InlineData("platform", "internal/v1/assistant/platform/answers", 'b')]
    public async Task MinimalRequestUsesFixedDestinationAndPurposeCredential(string audience, string path, char token)
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer(audience), request =>
        {
            Assert.Equal(new Uri("http://127.0.0.1:5122/" + path), request.RequestUri);
            Assert.Equal(new string(token, 64), request.Headers.GetValues("X-Monergy-Knowledge-Runtime").Single());
            Assert.Single(request.Headers); Assert.Null(request.Headers.Authorization);
        }));
        var answer = await client.AnswerAsync(audience, new("1.0.0", Question), CancellationToken.None);
        Assert.Equal("ANSWERED", answer.Projection.State); Assert.NotEmpty(answer.Projection.Sources);
        var browser = JsonSerializer.Serialize(answer.Projection, AssistantContract.Json);
        Assert.DoesNotContain("provenance", browser, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("knowledgeRevision", browser, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("catalogue")][InlineData("retrieval")][InlineData("audience")]
    public async Task ResponseRevisionHeadersAreIndependentlyBound(string header)
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer("platform"), badHeader: header));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnswerAsync("platform", new("1.0.0", Question), CancellationToken.None));
    }

    [Theory]
    [InlineData("question")][InlineData("scope")][InlineData("text")][InlineData("claim-status")][InlineData("provenance")]
    [InlineData("model")][InlineData("knowledge")][InlineData("source")][InlineData("timestamp")][InlineData("extra")][InlineData("claim-extra")]
    public async Task MutatedResponseIsRejected(string mutation)
    {
        var answer = Answer("platform");
        switch (mutation)
        {
            case "question": answer["question"] = "different"; break;
            case "scope": answer["scope"] = "PRODUCT_KNOWLEDGE"; break;
            case "text": answer["answer"] = "Invented prose"; break;
            case "claim-status": answer["claims"]![0]!["knowledgeStatus"] = "UNRESOLVED"; break;
            case "provenance": answer["claims"]![0]!["provenance"]![0]!["commit"] = new string('0', 40); break;
            case "model": answer["modelProcessing"]!["state"] = "USED"; break;
            case "knowledge": answer["knowledgeRevision"] = new string('0', 64); break;
            case "source": answer["sourceRevision"]!["application"] = new string('0', 40); break;
            case "timestamp": answer["recordedAt"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"); break;
            case "extra": answer["unapproved"] = "value"; break;
            case "claim-extra": answer["claims"]![0]!["unapproved"] = "value"; break;
        }
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(answer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnswerAsync("platform", new("1.0.0", Question), CancellationToken.None));
    }

    [Theory]
    [InlineData("What is Platform Administrator?")][InlineData("What is tenant onboarding?")]
    [InlineData("What does ReadyForAdmin mean?")][InlineData("How do tenant URLs work?")]
    [InlineData("How does workspace selection work?")][InlineData("Platform Administrator versus Organization Administrator")]
    public async Task ProductRestrictedQuestionsDoNotReachRuntime(string question)
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer("product"), _ => Assert.Fail("Restricted question reached Runtime.")));
        var result = await client.AnswerAsync("product", new("1.0.0", question), CancellationToken.None);
        Assert.Equal("UNSUPPORTED", result.Projection.State); Assert.Empty(result.Projection.Sources);
    }

    [Theory]
    [InlineData("product")][InlineData("platform")]
    public async Task EveryPermittedCanonicalClaimIsPreservedExactly(string audience)
    {
        using var catalogue = new AssistantKnowledgeCatalogue(CataloguePath, Configuration()["Monergy:Assistant:CatalogueSha256"]!);
        foreach (var claim in catalogue.Scope(audience).GetProperty("claims").EnumerateArray())
        {
            if (claim.GetProperty("knowledgeStatus").GetString() == "UNRESOLVED") continue;
            using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(Answer(audience, claimId: claim.GetProperty("claimId").GetString())));
            var answer = await client.AnswerAsync(audience, new("1.0.0", Question), default);
            Assert.Equal(claim.GetProperty("text").GetString(), Assert.Single(answer.Projection.Answer));
        }
    }

    [Theory]
    [InlineData("duplicate-field")][InlineData("duplicate-header")][InlineData("missing-header")]
    [InlineData("oversized")][InlineData("invalid-json")][InlineData("redirect")][InlineData("future-time")]
    public async Task MalformedTransportOrEnvelopeFailsClosed(string fault)
    {
        var answer = Answer("platform");
        if (fault == "future-time") answer["recordedAt"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        var raw = fault switch { "duplicate-field" => answer.ToJsonString().Replace("\"state\":\"ANSWERED\"", "\"state\":\"ANSWERED\",\"state\":\"ANSWERED\"", StringComparison.Ordinal),
            "oversized" => new string(' ', 140000) + answer.ToJsonString(), "invalid-json" => "{", _ => null };
        using var client = new AssistantKnowledgeClient(Configuration(), new RuntimeTransport(answer, raw: raw,
            status: fault == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK, mutate: response =>
            {
                if (fault == "duplicate-header") response.Headers.Add("X-Monergy-Knowledge-Catalogue", new string('a', 64));
                if (fault == "missing-header") response.Headers.Remove("X-Monergy-Knowledge-Audience");
                if (fault == "redirect") response.Headers.Location = new Uri("https://example.invalid/unapproved");
            }));
        var error = await Record.ExceptionAsync(() => client.AnswerAsync("platform", new("1.0.0", Question), default));
        Assert.NotNull(error); Assert.True(error is InvalidOperationException or JsonException or HttpRequestException);
    }

    [Fact]
    public async Task TimeoutIsBoundedAndDoesNotProduceAnAnswer()
    {
        using var client = new AssistantKnowledgeClient(Configuration(), new WaitingTransport());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AnswerAsync("platform", new("1.0.0", Question), default));
    }
    private sealed class WaitingTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return new(); }
    }
}
