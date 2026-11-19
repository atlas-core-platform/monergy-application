using System.Text.Json;

namespace Monergy.Platform;

public sealed class AssistantKnowledgeCatalogue : IDisposable
{
    private readonly JsonDocument document;
    public string Digest { get; }
    public string KnowledgeRevision => document.RootElement.GetProperty("knowledgeRevision").GetString()!;
    public string RetrievalRevision => document.RootElement.GetProperty("retrievalRevision").GetString()!;
    public string AudienceRevision => document.RootElement.GetProperty("audienceRevision").GetString()!;
    public JsonElement SourceRevision => document.RootElement.GetProperty("sourceRevision");

    public AssistantKnowledgeCatalogue(string path, string expectedDigest)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 262144) throw new InvalidOperationException("Assistant catalogue unavailable.");
        var bytes = File.ReadAllBytes(path);
        Digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (Digest != expectedDigest) throw new InvalidOperationException("Assistant catalogue digest mismatch.");
        document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireFields(root, "contractVersion", "knowledgeRevision", "sourceRevision", "retrievalRevision", "audienceRevision", "scopes");
        if (root.GetProperty("contractVersion").GetString() != "1.0.0" ||
            KnowledgeRevision != "0e4020190ff2e204ab582fdcf1075a8ef90562845b502ddd695ecd9ed213039a")
            throw new InvalidOperationException("Assistant catalogue contract mismatch.");
        RequireFields(SourceRevision, "application", "architecture", "accessManagement");
        if (SourceRevision.GetProperty("application").GetString() != "995d0a30991ad2a14a464ade62e0ed948f3e6914" ||
            SourceRevision.GetProperty("architecture").GetString() != "80cb94953c2d67c9eee568d333ae3435532af0d8" ||
            SourceRevision.GetProperty("accessManagement").GetString() != "85cb70159a1a8f700c2e15d191fb8097651c4b78" ||
            !DigestValue(RetrievalRevision) || !DigestValue(AudienceRevision))
            throw new InvalidOperationException("Assistant catalogue source mismatch.");
        RequireFields(root.GetProperty("scopes"), "product", "platform");
        foreach (var audience in new[] { "product", "platform" })
        {
            var scope = Scope(audience);
            RequireFields(scope, "claims", "sourceLabels", "topicIds", "questionPolicy");
            RequireFields(scope.GetProperty("questionPolicy"), "deniedPhrases", "deniedCombinations");
            if (scope.GetProperty("claims").GetArrayLength() is < 1 or > 100) throw new InvalidOperationException("Assistant catalogue claims invalid.");
            foreach (var label in scope.GetProperty("sourceLabels").EnumerateObject())
                if (label.Value.GetString() is not ("Role guide" or "Feature guide" or "Platform guide" or "Reviewed guidance"))
                    throw new InvalidOperationException("Assistant source label invalid.");
        }
    }

    public JsonElement Scope(string audience) => audience is "product" or "platform"
        ? document.RootElement.GetProperty("scopes").GetProperty(audience)
        : throw new InvalidOperationException("Assistant audience invalid.");
    public bool TopicAllowed(string audience, string? topic) => topic is null ||
        Scope(audience).GetProperty("topicIds").EnumerateObject().Any(item => item.Value.GetString() == topic);
    public bool QuestionAllowed(string audience, string question)
    {
        var normalized = question.Normalize(System.Text.NormalizationForm.FormKC);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\p{Cf}", "", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        normalized = " " + System.Text.RegularExpressions.Regex.Replace(normalized.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim() + " ";
        var policy = Scope(audience).GetProperty("questionPolicy");
        bool Contains(JsonElement phrase) => normalized.Contains(" " + phrase.GetString() + " ", StringComparison.Ordinal);
        return !policy.GetProperty("deniedPhrases").EnumerateArray().Any(Contains) &&
            !policy.GetProperty("deniedCombinations").EnumerateArray().Any(combination => combination.EnumerateArray().All(Contains));
    }
    public static bool DigestValue(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    public static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Assistant object invalid.");
        var keys = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (keys.Length != fields.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            fields.Any(field => !value.TryGetProperty(field, out _))) throw new InvalidOperationException("Assistant fields invalid.");
    }
    public void Dispose() => document.Dispose();
}
