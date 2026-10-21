using System.Globalization;
using System.Net.Http.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.AiIntelligence.Qa;

public sealed record QaCustomer(CustomerOwnerSession Session, CustomerOwnerWorkspace Workspace, CustomerAccessDecision Decision);
public interface IQaOwners
{
    Task<QaCustomer> Authorize(HttpContext http);
    Task<QaConfiguration> Configuration(HttpContext http);
    Task<QaModelResult> Generate(HttpContext http, QaModelRequest body);
    Task<IReadOnlyList<QaClaim>> Retrieve(HttpContext http, QaCustomer customer, string query, List<string> searchIds);
    Task<IReadOnlyList<QaClaim>> Document(HttpContext http, QaCustomer customer, string version);
    Task<IReadOnlyList<QaClaim>> Calculate(HttpContext http, QaCustomer customer, string position, string operation);
    Task Validate(HttpContext http, QaCustomer customer, IEnumerable<QaClaim> claims);
}
public sealed class QaOwners(CustomerOwnerClient identity, TenantAccessClient access, IConfiguration configuration, HttpClient client) : IQaOwners, IDisposable
{
    public async Task<QaCustomer> Authorize(HttpContext http)
    {
        var session = await identity.SessionAsync(http);
        var workspace = await identity.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted);
        if (workspace is null || !workspace.Active || !workspace.WorkspaceReady || workspace.OwnerActorId != session.ActorId)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        CustomerAccessDecision? baseline = null;
        foreach (var capability in new[] { "ai-intelligence.analysis.execute", "search.query.execute", "financial-profile.profile.read", "evidence.document.read" })
        {
            var decision = await access.EvaluateCustomerAsync(http, capability, workspace.CustomerId);
            if (decision.Outcome != "Allow" || decision.AccessBasis != "Self" || decision.ActorId != session.ActorId || decision.SubjectVersion != session.SubjectVersion || decision.CustomerVersion != workspace.Version)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if (baseline is not null) CustomerReviewHttp.Confirm(baseline, decision);
            baseline ??= decision;
        }
        return new(session, workspace, baseline!);
    }
    public Task<QaConfiguration> Configuration(HttpContext http) => Model<QaConfiguration>(http, "configuration", null);
    public Task<QaModelResult> Generate(HttpContext http, QaModelRequest body) => Model<QaModelResult>(http, "generate", body);
    private async Task<T> Model<T>(HttpContext http, string route, QaModelRequest? body)
    {
        using var request = Message(http, new Uri(Root("IntegrationGatewayUrl"), "internal/local/v1/ai-model/" + route), body is null ? HttpMethod.Get : HttpMethod.Post);
        request.Headers.Add("X-Monergy-Ai-Token", TenantAccessIntegration.Required(configuration, "Monergy:FinancialQa:InternalToken"));
        if (body is not null) request.Content = JsonContent.Create(body, options: ContractJson.Options);
        return await Read<T>(http, request);
    }
    public async Task<IReadOnlyList<QaClaim>> Retrieve(HttpContext http, QaCustomer customer, string query, List<string> searchIds)
    {
        var result = await Contract<SearchFinancialData, SearchResults>(http, customer, "SearchRetrievalUrl", "cid-043",
            SearchContractNames.SearchFinancialData, new(customer.Workspace.CustomerId, query, SearchMatchMode.Lexical, 20));
        if (result.Query != query || result.MatchMode != SearchMatchMode.Lexical || result.Items.IsDefault || result.Items.Length > 20)
            throw Invalid();
        var claims = new List<QaClaim>();
        foreach (var hit in result.Items)
        {
            if (hit.ResultType != SearchResultType.Financial || hit.AuthoritativeOwner != "Financial Profile Service" ||
                hit.RepresentationState != "DERIVED_REBUILDABLE" || hit.Source.SourceObjectType != "FinancialFact" ||
                !NetWorthData.Text(hit.SearchRecordId, 200) || !NetWorthData.Text(hit.Source.SourceObjectId, 200)) throw Invalid();
            var claim = await Fact(http, customer, hit.Source.SourceObjectId, null);
            if (claim.ProvenanceId != hit.Source.ProvenanceReferenceId) throw Invalid();
            searchIds.Add(hit.SearchRecordId);
            if (claims.All(x => x.SourceId != claim.SourceId)) claims.Add(claim with { Id = "source-" + claims.Count.ToString(CultureInfo.InvariantCulture) });
        }
        return claims;
    }
    public async Task Validate(HttpContext http, QaCustomer customer, IEnumerable<QaClaim> claims)
    {
        foreach (var group in claims.GroupBy(claim => claim.Kind + "|" + (claim.CalculationId ?? claim.SourceId)))
        {
            var first = group.First();
            IReadOnlyList<QaClaim> canonical;
            if (first.Kind == "DocumentExcerpt") canonical = await Document(http, customer, first.DocumentVersionId);
            else if (first.Kind == "Calculation") canonical = await CalculationClaims(http, customer, first.CalculationId!);
            else if (first.Kind == "FinancialFact") canonical = [await Fact(http, customer, first.SourceId, first.Revision)];
            else throw Invalid();
            foreach (var claim in group)
            {
                var match = canonical.SingleOrDefault(value => value with { Id = claim.Id } == claim);
                if (match is null) throw Invalid();
            }
        }
    }
    public async Task<IReadOnlyList<QaClaim>> Document(HttpContext http, QaCustomer customer, string version)
    {
        if (!TenantAccessClient.Identifier(version, 160)) throw new TenantBoundaryException("AI_DOCUMENT_INVALID", 400);
        using var request = Message(http, new Uri(Root("DocumentIntelligenceUrl"), "local/v1/customers/" + Uri.EscapeDataString(customer.Workspace.CustomerId) + "/documents/" + Uri.EscapeDataString(version) + "/qa-passages"), HttpMethod.Get);
        var result = await Read<QaDocumentPassages>(http, request);
        if (result.CustomerId != customer.Workspace.CustomerId || result.DocumentVersionId != version || result.ContentSha256.Length != 64 ||
            !result.ContentSha256.All(char.IsAsciiHexDigit) || !NetWorthData.Text(result.ReaderVersion, 160) || result.Passages is null || result.Passages.Count > 20 ||
            result.State is not ("AVAILABLE" or "UNSUPPORTED" or "UNRELIABLE_TEXT" or "NO_TEXT") || result.State != "AVAILABLE" && result.Passages.Count != 0 ||
            result.Passages.Any(x => x is null || !NetWorthData.Text(x.Id, 80) || string.IsNullOrWhiteSpace(x.Text) || x.Text.Length > 1500 || !NetWorthData.Text(x.Location, 500)) ||
            result.Passages.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Passages.Count) throw Invalid();
        return result.Passages.Select(x => new QaClaim(x.Id, "Document quotation/transcription (not verified financial truth): " + x.Text,
            "DocumentExcerpt", version, 1, result.ReaderVersion, version, result.ContentSha256, x.Location)).ToArray();
    }
    public async Task<IReadOnlyList<QaClaim>> Calculate(HttpContext http, QaCustomer customer, string position, string operation)
    {
        if (!TenantAccessClient.Identifier(position, 80) || !NetWorthData.Operation(operation)) throw new TenantBoundaryException("AI_CALCULATION_INVALID", 400);
        await RequireCalculation(http, customer);
        var input = await Position(http, customer, position);
        using var request = Message(http, NetWorthUri(customer, "FinancialRulesUrl", "calculations"), HttpMethod.Post);
        request.Content = JsonContent.Create(new CalculateNetWorth(operation, position), options: ContractJson.Options);
        var result = await Read<NetWorthCalculation>(http, request);
        if (!NetWorthData.Calculation(result, customer.Workspace.CustomerId) || result.OperationId != operation || result.RecordedBy != customer.Session.ActorId || !NetWorthData.Same(result.Position, input)) throw Invalid();
        return await CalculationClaims(http, customer, result.CalculationId);
    }
    private async Task<IReadOnlyList<QaClaim>> CalculationClaims(HttpContext http, QaCustomer customer, string id)
    {
        if (!TenantAccessClient.Identifier(id, 80)) throw Invalid();
        await RequireCalculation(http, customer);
        using var request = Message(http, NetWorthUri(customer, "FinancialRulesUrl", "calculations/" + Uri.EscapeDataString(id)), HttpMethod.Get);
        var result = await Read<NetWorthCalculation>(http, request);
        if (!NetWorthData.Calculation(result, customer.Workspace.CustomerId) || result.CalculationId != id) throw Invalid();
        var position = await Position(http, customer, result.Position.PositionId);
        if (!NetWorthData.Same(result.Position, position)) throw Invalid();
        await RequireCalculation(http, customer);
        if (result.State != "CALCULATED") return [new QaClaim("calculation-incomplete",
            "Financial Rules could not calculate from this inventory. Missing or incompatible inputs: " + string.Join(", ", result.Issues),
            "Calculation", position.PositionId, 1, result.DefinitionHash, "", null, position.Position.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), result.CalculationId, result.RuleVersion)];
        var date = position.Position.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        QaClaim Claim(string code, string label, string? value) => new("calculation-" + code,
            $"Financial Rules calculated {label}: {value} {position.Position.Currency}, as of {date}, from the selected customer-reviewed inventory. These values are not externally verified.",
            "Calculation", position.PositionId, 1, result.DefinitionHash, "", null, date, result.CalculationId, result.RuleVersion);
        return [Claim("assets", "total assets", result.TotalAssets), Claim("liabilities", "total liabilities", result.TotalLiabilities), Claim("net-worth", "net worth", result.NetWorth)];
    }
    private async Task<ReviewedNetWorthPosition> Position(HttpContext http, QaCustomer customer, string id)
    {
        using var request = Message(http, NetWorthUri(customer, "FinancialProfileUrl", "positions/" + Uri.EscapeDataString(id)), HttpMethod.Get);
        var value = await Read<ReviewedNetWorthPosition>(http, request);
        if (!NetWorthData.Bound(value, customer.Workspace.CustomerId, DateTimeOffset.UtcNow) || value.PositionId != id) throw Invalid();
        return value;
    }
    private Uri NetWorthUri(QaCustomer customer, string owner, string suffix) => new(Root(owner),
        "local/v1/customers/" + Uri.EscapeDataString(customer.Workspace.CustomerId) + "/net-worth/" + suffix);
    private async Task RequireCalculation(HttpContext http, QaCustomer customer)
    {
        var decision = await access.EvaluateCustomerAsync(http, "financial-rules.calculation.execute", customer.Workspace.CustomerId);
        if (decision.Outcome != "Allow" || decision.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
        CustomerReviewHttp.Confirm(customer.Decision, decision);
    }

    private async Task<QaClaim> Fact(HttpContext http, QaCustomer customer, string id, int? revision)
    {
        var cid = customer.Workspace.CustomerId;
        var fact = await Contract<GetFinancialFact, AuthoritativeFinancialFact>(http, customer, "FinancialProfileUrl", "cid-032", Vs02ContractNames.GetFinancialFact, new(id, cid, revision));
        if (fact.CustomerId != cid || fact.FinancialFactId != id || fact.Revision < 1 || revision is not null && fact.Revision != revision ||
            !NetWorthData.Text(fact.Label, 500) || !NetWorthData.Currency(fact.Currency)) throw Invalid();
        var provenance = await Contract<GetFinancialProvenance, FinancialProvenance>(http, customer, "FinancialProfileUrl", "cid-033", Vs02ContractNames.GetFinancialProvenance, new(fact.FinancialProvenanceId, cid));
        if (provenance.CustomerId != cid || provenance.FinancialFactId != id || provenance.FinancialProvenanceId != fact.FinancialProvenanceId) throw Invalid();
        var evidence = await Contract<GetEvidenceReference, EvidenceReference>(http, customer, "EvidenceUrl", "cid-021", Vs02ContractNames.GetEvidenceReference, new(provenance.DocumentVersionId, cid));
        if (evidence.CustomerId != cid || evidence.DocumentVersionId != provenance.DocumentVersionId || evidence.EvidenceId != provenance.EvidenceId) throw Invalid();
        return new("", $"Source record: {fact.Label} — {fact.Value.ToString(CultureInfo.InvariantCulture)} {fact.Currency}, effective {fact.EffectiveDate:yyyy-MM-dd}. Category: {fact.FactType}. This is a recorded source amount, not a verified balance, valuation, coverage amount or assessed tax.",
            "FinancialFact", id, fact.Revision, fact.FinancialProvenanceId, provenance.DocumentVersionId);
    }
    private async Task<TData> Contract<TPayload, TData>(HttpContext http, QaCustomer customer, string owner, string contractId, string name, TPayload payload)
    {
        var request = new ContractRequest<TPayload>(name, ContractGuard.CurrentVersion, "qa-request-" + Guid.NewGuid().ToString("N"),
            "qa-correlation-" + Guid.NewGuid().ToString("N"), null,
            new(new(customer.Session.ActorId, "Customer", DateTimeOffset.UtcNow, customer.Session.AuthenticationContextId),
                new("ai-intelligence", "local-uat-workspace:" + customer.Session.TenantId),
                new("customer-advice", null, customer.Decision.DecisionId, customer.Workspace.CustomerId)), null, payload);
        using var message = Message(http, new Uri(Root(owner), "contracts/" + contractId + "/v1"), HttpMethod.Post);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        var result = await Read<ContractResult<TData>>(http, message);
        if (result.ContractName != name || result.ContractVersion != request.ContractVersion || result.RequestId != request.RequestId || result.CorrelationId != request.CorrelationId) throw Invalid();
        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw new TenantBoundaryException("AI_SOURCE_UNAVAILABLE", result.Error?.Category is ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired ? 403 : 503);
        return result.Data;
    }
    private static HttpRequestMessage Message(HttpContext http, Uri uri, HttpMethod method)
    {
        var (tenant, session) = TenantAccessClient.Headers(http); var message = new HttpRequestMessage(method, uri);
        message.Headers.Add("X-Monergy-Tenant", tenant); message.Headers.Add("X-Monergy-Session", session); return message;
    }
    private async Task<T> Read<T>(HttpContext http, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, http.RequestAborted);
        if (!response.IsSuccessStatusCode) throw new TenantBoundaryException("AI_OWNER_UNAVAILABLE", (int)response.StatusCode is 401 or 403 or 409 ? (int)response.StatusCode : 503);
        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, http.RequestAborted) ?? throw Invalid();
    }
    private Uri Root(string name) => TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:FinancialQa:" + name));
    private static TenantBoundaryException Invalid() => new("AI_SOURCE_RESPONSE_INVALID", 503);
    public void Dispose() => client.Dispose();
}
