using System.Collections.Immutable;

namespace Monergy.UatGateway;

public sealed record ProductSearchCapabilityDecision(
    string CapabilityId,
    string DecisionId,
    string Outcome);

public sealed record ProductSearchAuthorizationReceipt(
    string Scope,
    string SearchDecisionId,
    ImmutableArray<string> CapabilityIds);

public sealed class ProductSearchAuthorizationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class ProductSearchAuthorizationPolicy
{
    public const string SearchCapability = "search.query.execute";
    public const string DocumentCapability = "evidence.document.read";
    public const string FinancialCapability = "financial-profile.profile.read";

    private static readonly ImmutableArray<string> AllCapabilities =
        [SearchCapability, DocumentCapability, FinancialCapability];
    private static readonly ImmutableArray<string> DocumentCapabilities =
        [SearchCapability, DocumentCapability];
    private static readonly ImmutableArray<string> FinancialCapabilities =
        [SearchCapability, FinancialCapability];

    public static ImmutableArray<string> RequiredCapabilities(string scope) => scope switch
    {
        "all" => AllCapabilities,
        "documents" => DocumentCapabilities,
        "financial" => FinancialCapabilities,
        _ => throw new ProductSearchAuthorizationException("SEARCH_SCOPE_INVALID"),
    };

    public static async Task<ProductSearchAuthorizationReceipt> RequireAsync(
        string scope,
        Func<string, CancellationToken, Task<ProductSearchCapabilityDecision>> evaluate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        var required = RequiredCapabilities(scope);
        string? searchDecisionId = null;
        foreach (var capability in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await evaluate(capability, cancellationToken).ConfigureAwait(false);
            if (decision.CapabilityId != capability ||
                decision.Outcome is not ("Allow" or "Deny") ||
                decision.Outcome == "Allow" && string.IsNullOrWhiteSpace(decision.DecisionId))
                throw new ProductSearchAuthorizationException("SEARCH_AUTHORIZATION_RESPONSE_INVALID");
            if (decision.Outcome != "Allow")
                throw new ProductSearchAuthorizationException(capability switch
                {
                    SearchCapability => "SEARCH_ACCESS_DENIED",
                    DocumentCapability => "SEARCH_DOCUMENT_ACCESS_DENIED",
                    FinancialCapability => "SEARCH_FINANCIAL_ACCESS_DENIED",
                    _ => "SEARCH_ACCESS_DENIED",
                });
            if (capability == SearchCapability) searchDecisionId = decision.DecisionId;
        }

        return new(scope, searchDecisionId!, required);
    }
}
