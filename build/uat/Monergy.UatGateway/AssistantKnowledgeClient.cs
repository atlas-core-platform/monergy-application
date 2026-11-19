using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.UatGateway;

internal sealed record AssistantQuestion(string ContractVersion, string Question);
internal sealed record AssistantAnswer(string State, string[] Answer, string[] Sources, bool CanSuggest);
internal sealed record AnonymousAssistantCounter(string ContractVersion, string SourceAudience, string Category,
    string Intent, string KnowledgeRevision, string RetrievalRevision);
internal sealed record ValidatedAssistantAnswer(AssistantAnswer Projection, AnonymousAssistantCounter Counter);

internal sealed class AssistantKnowledgeClient(IConfiguration configuration) : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 131072 };
    private readonly Lazy<AssistantKnowledgeCatalogue> catalogue = new(() => new(configuration["Monergy:Assistant:CataloguePath"] ?? "",
        configuration["Monergy:Assistant:CatalogueSha256"] ?? ""));
    private static readonly Dictionary<string, string> Diagnostics = new(StringComparer.Ordinal)
    {
        ["INSUFFICIENT_GROUNDING"] = "The available Monergy knowledge is insufficient for this question.",
        ["UNKNOWN"] = "Monergy has no governed knowledge for this question.",
        ["UNRESOLVED"] = "This decision remains unresolved.",
        ["NOT_IMPLEMENTED"] = "This capability is not implemented in the recorded scope.",
        ["UNSUPPORTED"] = "This request is outside the approved knowledge scope.",
        ["CONFLICT"] = "ARCHITECTURE_CONFLICT: the recorded sources disagree.",
        ["DEPENDENCY_UNAVAILABLE"] = "KNOWLEDGE_REFRESH_REQUIRED: current verified knowledge is unavailable.",
    };
    public AssistantKnowledgeCatalogue Catalogue => catalogue.Value;
    private Uri Destination(string path)
    {
        var configured = configuration["Monergy:Assistant:RuntimeUrl"] ?? "http://127.0.0.1:5122/";
        if (configured.TrimEnd('/') != "http://127.0.0.1:5122") throw new InvalidOperationException("Assistant destination invalid.");
        return new Uri("http://127.0.0.1:5122/" + path);
    }
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(Destination("health/ready"), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            AssistantKnowledgeCatalogue.RequireFields(root, "status", "contractVersion", "knowledgeRevision", "sourceRevision", "retrievalRevision", "audienceRevision", "catalogueSha256");
            return root.GetProperty("status").GetString() == "ready" && root.GetProperty("contractVersion").GetString() == "1.0.0" &&
                root.GetProperty("knowledgeRevision").GetString() == Catalogue.KnowledgeRevision &&
                JsonElement.DeepEquals(root.GetProperty("sourceRevision"), Catalogue.SourceRevision) &&
                root.GetProperty("retrievalRevision").GetString() == Catalogue.RetrievalRevision &&
                root.GetProperty("audienceRevision").GetString() == Catalogue.AudienceRevision && root.GetProperty("catalogueSha256").GetString() == Catalogue.Digest;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    public async Task<ValidatedAssistantAnswer> AnswerAsync(string audience, AssistantQuestion question, CancellationToken ct)
    {
        if (question.ContractVersion != "1.0.0" || question.Question.Length is < 1 or > 2000 ||
            question.Question.Any(c => char.IsControl(c) && c is not '\n' and not '\t' and not '\r') ||
            AssistantInputSafety.ContainsProtectedMaterial(question.Question))
            throw new TenantBoundaryException("ASSISTANT_QUESTION_INVALID", 400);
        var token = configuration["Monergy:Assistant:" + (audience == "product" ? "ProductToken" : "PlatformToken")];
        if (token is not { Length: >= 32 } || configuration["Monergy:Assistant:ProductToken"] == configuration["Monergy:Assistant:PlatformToken"])
            throw new InvalidOperationException("Assistant workload unavailable.");
        if (!Catalogue.QuestionAllowed(audience, question.Question) || AssistantInputSafety.ContainsUnsafeInstruction(question.Question))
            return Result(audience, "UNSUPPORTED", [Diagnostics["UNSUPPORTED"]], [], "UNCLASSIFIED");
        using var request = new HttpRequestMessage(HttpMethod.Post, Destination(audience == "product" ? "internal/v1/assistant/answers" : "internal/v1/assistant/platform/answers"));
        request.Headers.Add("X-Monergy-Knowledge-Runtime", token);
        request.Content = JsonContent.Create(question, options: AssistantContract.Json);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new TenantBoundaryException("ASSISTANT_QUESTION_INVALID", 400);
            throw new InvalidOperationException("Assistant dependency unavailable.");
        }
        bool Pinned(string header, string expected) => response.Headers.TryGetValues(header, out var values) &&
            values.Count() == 1 && values.Single() == expected;
        if (!Pinned("X-Monergy-Knowledge-Catalogue", Catalogue.Digest) ||
            !Pinned("X-Monergy-Knowledge-Retrieval", Catalogue.RetrievalRevision) ||
            !Pinned("X-Monergy-Knowledge-Audience", Catalogue.AudienceRevision))
            throw new InvalidOperationException("Assistant response knowledge revision invalid.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        AssistantKnowledgeCatalogue.RequireFields(root, "contractVersion", "answerId", "state", "question", "answer", "scope", "claims", "limitations", "nextActions", "knowledgeRevision", "sourceRevision", "modelProcessing", "recordedAt");
        var state = root.GetProperty("state").GetString() ?? "";
        if (root.GetProperty("contractVersion").GetString() != "1.0.0" || root.GetProperty("question").GetString() != question.Question ||
            !AssistantContract.OpaqueId(root.GetProperty("answerId").GetString() ?? "") ||
            root.GetProperty("scope").GetString() != (audience == "product" ? "PRODUCT_KNOWLEDGE" : "PLATFORM_KNOWLEDGE") ||
            root.GetProperty("knowledgeRevision").GetString() != Catalogue.KnowledgeRevision ||
            !JsonElement.DeepEquals(root.GetProperty("sourceRevision"), Catalogue.SourceRevision) ||
            root.GetProperty("limitations").GetArrayLength() != 0 || root.GetProperty("nextActions").GetArrayLength() != 0 ||
            !DateTimeOffset.TryParse(root.GetProperty("recordedAt").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var recorded) || recorded.Offset != TimeSpan.Zero ||
            recorded < DateTimeOffset.UtcNow.AddMinutes(-1) || recorded > DateTimeOffset.UtcNow.AddSeconds(5))
            throw new InvalidOperationException("Assistant response contract invalid.");
        var model = root.GetProperty("modelProcessing");
        AssistantKnowledgeCatalogue.RequireFields(model, "state");
        if (model.GetProperty("state").GetString() != "NOT_USED") throw new InvalidOperationException("Assistant model processing invalid.");
        var claims = root.GetProperty("claims").EnumerateArray().ToArray();
        var answer = root.GetProperty("answer").GetString();
        if (state != "ANSWERED")
        {
            if (!Diagnostics.TryGetValue(state, out var expected) || claims.Length != 0 || answer != expected)
                throw new InvalidOperationException("Assistant diagnostic invalid.");
            if (state == "DEPENDENCY_UNAVAILABLE") throw new InvalidOperationException("Assistant dependency unavailable.");
            return Result(audience, state, [expected], [], "UNCLASSIFIED");
        }
        if (claims.Length is < 1 or > 20) throw new InvalidOperationException("Assistant claims invalid.");
        var scope = Catalogue.Scope(audience);
        var canonical = scope.GetProperty("claims").EnumerateArray().ToArray();
        var texts = new List<string>(); var labels = new List<string>(); var topics = new List<string>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            var id = claim.GetProperty("claimId").GetString() ?? "";
            if (!ids.Add(id) || !canonical.Any(value => JsonElement.DeepEquals(value, claim)))
                throw new InvalidOperationException("Assistant claim provenance invalid.");
            texts.Add(claim.GetProperty("text").GetString()!);
            labels.Add(scope.GetProperty("sourceLabels").GetProperty(id).GetString()!);
            topics.Add(scope.GetProperty("topicIds").GetProperty(id).GetString()!);
        }
        if (answer != string.Join('\n', texts)) throw new InvalidOperationException("Assistant generated text rejected.");
        return Result(audience, state, [.. texts], [.. labels.Distinct(StringComparer.Ordinal)], topics.Distinct(StringComparer.Ordinal).Count() == 1 ? topics[0] : "UNCLASSIFIED");
    }
    private ValidatedAssistantAnswer Result(string audience, string state, string[] answers, string[] sources, string intent) =>
        new(new(state, answers, sources, state != "ANSWERED"), new("1.0.0", audience, state, intent, Catalogue.KnowledgeRevision, Catalogue.RetrievalRevision));
    public void Dispose() { client.Dispose(); if (catalogue.IsValueCreated) catalogue.Value.Dispose(); }
}
