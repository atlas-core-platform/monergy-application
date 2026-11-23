using System.Text.RegularExpressions;
using Monergy.Platform;

namespace Monergy.Services.AiIntelligence.AssistantFeedback;

internal static partial class FeedbackRules
{
    public static readonly string[] Statuses = ["New", "Under review", "Awaiting decision", "Ready for verification", "Resolved", "Duplicate", "Out of scope"];
    public static readonly string[] Classifications = ["Knowledge gap", "Retrieval gap", "Clarification gap", "Decision follow-up", "Product request", "Conflict resolution", "Audience restriction", "Outside product knowledge"];
    public static bool SafeQuestion(string value) => value.Length is >= 5 and <= 500 &&
        !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') &&
        !AssistantInputSafety.ContainsProtectedMaterial(value) && !AssistantInputSafety.ContainsUnsafeInstruction(value) &&
        !Sensitive().IsMatch(AssistantInputSafety.Normalize(value)) && !Digits().IsMatch(value) && !Email().IsMatch(value) &&
        !UnsafeOrPersonal().IsMatch(AssistantInputSafety.Normalize(value));
    [GeneratedRegex(@"(?i)(?:ignore\s+(?:all\s+)?(?:previous|prior|system)\s+instructions|(?:reveal|show|print)\s+(?:the\s+)?(?:system\s+prompt|credentials|secrets)|customer\s+(?:named|called)|(?:my|our|their|his|her|customer'?s)\s+(?:salary|income|debt|balance|net\s+worth|pan|aadhaar|account|loan)|\b(?:bypass|override)\s+(?:authorization|permission|consent|security))", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeOrPersonal();
    [GeneratedRegex(@"(?i)(?:connection\s*string\s*[:=]|\b(?:pan|aadhaar|aadhar|iban|account\s*(?:number|no))\s*[:=])", RegexOptions.CultureInvariant)]
    private static partial Regex Sensitive();
    [GeneratedRegex(@"\d{4,}", RegexOptions.CultureInvariant)] private static partial Regex Digits();
    [GeneratedRegex(@"[^\s@]+@[^\s@]+", RegexOptions.CultureInvariant)] private static partial Regex Email();
    public static void Submission(HelpTopicSubmission request, AssistantKnowledgeCatalogue catalogue)
    {
        if (request.ContractVersion != "1.0.0" || !SafeQuestion(request.GeneralQuestion) ||
            request.SourceAudience is not ("product" or "platform") || !catalogue.TopicAllowed(request.SourceAudience, request.TopicId) ||
            request.KnowledgeRevision != catalogue.KnowledgeRevision || request.RetrievalRevision != catalogue.RetrievalRevision ||
            !AssistantKnowledgeCatalogue.DigestValue(request.IdempotencyKey))
            throw new TenantBoundaryException("HELP_TOPIC_INPUT_REJECTED", 400);
    }
    public static void Change(TopicChange request)
    {
        if (request.ContractVersion != "1.0.0" || request.ExpectedRevision < 1 ||
            !Statuses.Contains(request.Status, StringComparer.Ordinal) || !Classifications.Contains(request.Classification, StringComparer.Ordinal) ||
            request.TopicId is not null && !AssistantContract.Code(request.TopicId) ||
            request.OwnerReference is not null && !AssistantContract.Code(request.OwnerReference) ||
            request.DuplicateOf is not null && !AssistantContract.OpaqueId(request.DuplicateOf) ||
            request.Status == "Duplicate" && request.DuplicateOf is null || request.Status != "Duplicate" && request.DuplicateOf is not null ||
            request.Resolution is { } resolution && new[] { resolution.ApprovedChangeReference, resolution.PublishedVersion, resolution.VerificationReference }
                .Any(value => !AssistantContract.Code(value, 256)))
            throw new TenantBoundaryException("HELP_TOPIC_REVIEW_INVALID", 400);
    }
}
