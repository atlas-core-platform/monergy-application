using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AskFinancialQuestion(string OperationId, string Question, string ConfigurationRevision, bool AllowModelProcessing, string? DocumentVersionId = null, string? NetWorthPositionId = null, string? SummaryNetWorthCalculationId = null, string? SummaryCashflowCalculationId = null);
public sealed record QaConfiguration(bool Available, string Revision, string ProviderLabel, string Notice);
public sealed record QaClaim(string Id, string Text, string Kind, string SourceId, int Revision, string ProvenanceId, string DocumentVersionId, string? ContentHash = null, string? SourceLocation = null, string? CalculationId = null, string? RuleVersion = null);
public sealed record QaAnswer(string AnswerId, string OperationId, string Question, string State, string Introduction,
    IReadOnlyList<QaClaim> Claims, DateTimeOffset RecordedAt, string Model, string PromptVersion, string ConfigurationRevision);
public sealed record QaModelRequest(string Phase, string Question, string ConfigurationRevision, bool AllowModelProcessing, IReadOnlyList<QaClaim> Claims, string Basis = "financial");
public sealed record QaModelResult(string Model, string Content);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QaPlan(string Mode, string Query);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QaSelection(string State, IReadOnlyList<string> ClaimIds);

public sealed record QaDocumentPassages(string CustomerId, string DocumentVersionId, string ContentSha256,
    string ReaderVersion, string State, IReadOnlyList<QaDocumentPassage> Passages);
public sealed record QaDocumentPassage(string Id, string Text, string Location);
