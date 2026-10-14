namespace Monergy.Contracts;

public static class App02ContractNames
{
    public const string ListCustomerEvidence = "ListCustomerEvidence";
}

public sealed record ListCustomerEvidence(string CustomerId, int Limit);

public sealed record CustomerEvidenceItem(
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    int Version,
    string SourceName,
    string OriginalFileName,
    string ContentType,
    long ContentLength,
    string ContentSha256,
    DateTimeOffset ReceivedAt,
    DateTimeOffset CreatedAt);

public sealed record CustomerEvidenceLibrary(
    string CustomerId,
    IReadOnlyList<CustomerEvidenceItem> Items);
