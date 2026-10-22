using System.Security.Cryptography;
using System.Text;
using Monergy.Platform;

namespace Monergy.Services.DocumentIntelligence.Application;

public sealed record ClassificationReview(long Revision, string ActorId, string DocumentClass, string Reason, DateTimeOffset ReviewedAt);
public sealed record DocumentClassification(string TenantId, string CustomerId, string DocumentVersionId, string SourceHash,
    string ClassifierVersion, string AutomaticClass, string Reason, DateTimeOffset ClassifiedAt, long Revision,
    IReadOnlyList<ClassificationReview> Reviews)
{
    public string DocumentClass => Reviews.Count == 0 ? AutomaticClass : Reviews[^1].DocumentClass;
    public bool NeedsReview => Reviews.Count == 0 && AutomaticClass == "UNCLASSIFIED";
}
public sealed record ClassificationReviewPage(string TenantId, IReadOnlyList<DocumentClassification> Items, string? NextCursor, bool CanReview);
public sealed record ClassificationChange(long ExpectedRevision, string DocumentClass, string Reason);
public interface IDocumentClassificationRepository
{
    Task<DocumentClassification> RecordAsync(DocumentClassification candidate, CancellationToken ct);
    Task<DocumentClassification?> ReadAsync(string tenant, string customer, string version, CancellationToken ct);
    Task<IReadOnlyList<DocumentClassification>> ListAsync(string tenant, bool unknownOnly, string? after, CancellationToken ct);
    Task<DocumentClassification> ReviewAsync(string tenant, string customer, string version, string actor, ClassificationChange change, DateTimeOffset now, CancellationToken ct);
}
public static class ClassificationRules
{
    public const string Version = "document-classification/1.0.0";
    public static readonly string[] Classes = ["ITR", "FORM_16", "BANK_STATEMENT", "SALARY_SLIP", "CREDIT_REPORT", "LOAN", "INSURANCE", "INVESTMENT", "PROPERTY", "VEHICLE", "GST", "PAN", "AADHAAR", "OTHER", "UNCLASSIFIED"];
    private static readonly (string Class, string[] Titles)[] Titles = [
        ("ITR", ["INCOME TAX RETURN", "INDIAN INCOME TAX RETURN ACKNOWLEDGEMENT", "ITR ACKNOWLEDGEMENT", "ITR-V"]),
        ("FORM_16", ["FORM 16", "FORM NO. 16", "FORM NO 16"]),
        ("BANK_STATEMENT", ["BANK STATEMENT", "STATEMENT OF ACCOUNT", "SAVINGS ACCOUNT STATEMENT", "CURRENT ACCOUNT STATEMENT"]),
        ("SALARY_SLIP", ["SALARY SLIP", "PAY SLIP", "PAYSLIP"]),
        ("CREDIT_REPORT", ["CREDIT REPORT", "CREDIT INFORMATION REPORT", "CIBIL REPORT", "EXPERIAN CREDIT REPORT"]),
        ("LOAN", ["LOAN STATEMENT", "LOAN ACCOUNT STATEMENT", "LOAN SANCTION LETTER", "LOAN AGREEMENT"]),
        ("INSURANCE", ["INSURANCE POLICY", "LIFE INSURANCE POLICY", "HEALTH INSURANCE POLICY", "MOTOR INSURANCE POLICY"]),
        ("INVESTMENT", ["INVESTMENT STATEMENT", "MUTUAL FUND STATEMENT", "CONSOLIDATED ACCOUNT STATEMENT", "DEMAT ACCOUNT STATEMENT"]),
        ("PROPERTY", ["PROPERTY SALE DEED", "SALE DEED", "PROPERTY REGISTRATION DOCUMENT", "PROPERTY TAX RECEIPT"]),
        ("VEHICLE", ["VEHICLE PURCHASE INVOICE", "VEHICLE REGISTRATION CERTIFICATE"]),
        ("GST", ["GST RETURN", "GST REGISTRATION CERTIFICATE", "FORM GSTR-1", "FORM GSTR-3B", "FORM GST REG-06"]),
        ("PAN", ["PERMANENT ACCOUNT NUMBER CARD", "PAN CARD", "E-PAN CARD"]),
        ("AADHAAR", ["AADHAAR", "AADHAAR CARD", "E-AADHAAR"])
    ];
    public static (string Class, string Reason) Classify(IReadOnlyList<(string Text, decimal Confidence)> lines)
    {
        if (lines.Count == 0) return ("UNCLASSIFIED", "NO_TEXT");
        if (lines.Count > 1000 || lines.Any(x => x.Text.Length > 2000)) return ("UNCLASSIFIED", "TEXT_LIMIT");
        var matches = lines.Select((line, index) => (line, index, text: string.Join(' ', line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant()))
            .SelectMany(x => Titles.Where(t => t.Titles.Contains(x.text, StringComparer.Ordinal)).Select(t => (t.Class, x.index, x.line.Confidence))).ToArray();
        if (matches.Select(x => x.Class).Distinct().Count() > 1) return ("UNCLASSIFIED", "AMBIGUOUS_TITLES");
        if (matches.Length == 0 || matches.All(x => x.index >= 10)) return ("UNCLASSIFIED", "NO_SUPPORTED_TITLE");
        if (matches.Any(x => x.Confidence < 90)) return ("UNCLASSIFIED", "LOW_CONFIDENCE");
        return (matches[0].Class, "SUPPORTED_TITLE");
    }
    public static void ValidateChange(ClassificationChange change)
    {
        if (change.ExpectedRevision < 1 || !Classes.Contains(change.DocumentClass, StringComparer.Ordinal) ||
            change.DocumentClass == "UNCLASSIFIED" || change.Reason is not ("SOURCE_REVIEWED" or "CUSTOMER_CONFIRMED" or "OTHER_DOCUMENT"))
            throw new TenantBoundaryException("CLASSIFICATION_REVIEW_INVALID", 400);
    }
    public static DocumentClassification Apply(DocumentClassification current, string actor, ClassificationChange change, DateTimeOffset now)
    {
        ValidateChange(change);
        if (current.Revision != change.ExpectedRevision) throw new TenantBoundaryException("CLASSIFICATION_REVISION_CONFLICT", 409);
        if (current.Reviews.Count >= 100) throw new TenantBoundaryException("CLASSIFICATION_REVIEW_LIMIT", 409);
        return current with { Revision = current.Revision + 1, Reviews = current.Reviews.Append(new(current.Revision + 1, actor, change.DocumentClass, change.Reason, now)).ToArray() };
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Key(string customer, string version) => Hash(Encoding.UTF8.GetBytes(customer + "\n" + version));
    public static bool Valid(DocumentClassification row, string tenant) => row.TenantId == tenant &&
        TenantAccessClient.Identifier(row.CustomerId, 128) && TenantAccessClient.Identifier(row.DocumentVersionId, 160) &&
        row.SourceHash.Length == 64 && row.SourceHash.All(char.IsAsciiHexDigitLower) && row.ClassifierVersion == Version &&
        Classes.Contains(row.AutomaticClass, StringComparer.Ordinal) && row.ClassifiedAt >= DateTimeOffset.UnixEpoch &&
        row.Reason is "NO_TEXT" or "TEXT_LIMIT" or "AMBIGUOUS_TITLES" or "NO_SUPPORTED_TITLE" or "LOW_CONFIDENCE" or "SUPPORTED_TITLE" or "UNSUPPORTED_LAYOUT" &&
        row.Reviews.Count <= 100 && row.Revision == row.Reviews.Count + 1 && row.Reviews.Select((review, index) =>
            review.Revision == index + 2 && TenantAccessClient.Identifier(review.ActorId, 128) && Classes.Contains(review.DocumentClass, StringComparer.Ordinal) &&
            review.DocumentClass != "UNCLASSIFIED" && review.Reason is "SOURCE_REVIEWED" or "CUSTOMER_CONFIRMED" or "OTHER_DOCUMENT" && review.ReviewedAt >= DateTimeOffset.UnixEpoch).All(x => x);
}
