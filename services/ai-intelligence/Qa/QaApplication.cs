using System.Text.Json;
using System.Text.Json.Serialization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.AiIntelligence.Qa;

public sealed class QaApplication(IQaOwners owners, QaStore store)
{
    public const string PromptVersion = FinancialQaSafety.PromptVersion;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static QaPlan ParsePlan(string content)
    {
        var plan = Parse<QaPlan>(content);
        if (plan.Mode is not ("lookup" or "calculation" or "unsupported") || !NetWorthData.Text(plan.Query, 256) ||
            FinancialQaSafety.ContainsInstructionOverride(plan.Query)) throw Invalid();
        return plan;
    }
    public static IReadOnlyList<QaClaim> Select(string content, IReadOnlyList<QaClaim> claims)
    {
        var selection = Parse<QaSelection>(content);
        if (selection.State is not ("SOURCES" or "INSUFFICIENT_DATA") || selection.ClaimIds is null || selection.ClaimIds.Count > 10 ||
            selection.ClaimIds.Distinct(StringComparer.Ordinal).Count() != selection.ClaimIds.Count ||
            selection.State == "INSUFFICIENT_DATA" && selection.ClaimIds.Count != 0 || selection.State == "SOURCES" && selection.ClaimIds.Count == 0 ||
            selection.ClaimIds.Any(id => claims.All(x => x.Id != id))) throw Invalid();
        return selection.ClaimIds.Select(id => claims.Single(x => x.Id == id)).ToArray();
    }
    private static T Parse<T>(string content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        var properties = document.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
        if (properties.Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length) throw Invalid();
        return document.RootElement.Deserialize<T>(Json) ?? throw Invalid();
    }
    public async Task<QaAnswer> Ask(HttpContext http, AskFinancialQuestion input)
    {
        if (!NetWorthData.Operation(input.OperationId) || !NetWorthData.Text(input.Question, 1000) || !input.AllowModelProcessing || !NetWorthData.Text(input.ConfigurationRevision, 64))
            throw new TenantBoundaryException("AI_REQUEST_INVALID", 400);
        if (input.DocumentVersionId is not null && input.NetWorthPositionId is not null ||
            input.DocumentVersionId is not null && !TenantAccessClient.Identifier(input.DocumentVersionId, 160) ||
            input.NetWorthPositionId is not null && !TenantAccessClient.Identifier(input.NetWorthPositionId, 80)) throw new TenantBoundaryException("AI_REQUEST_INVALID", 400);
        var summary=input.SummaryNetWorthCalculationId is not null||input.SummaryCashflowCalculationId is not null;
        if(summary&&(input.DocumentVersionId is not null||input.NetWorthPositionId is not null||
            !TenantAccessClient.Identifier(input.SummaryNetWorthCalculationId,80)||!TenantAccessClient.Identifier(input.SummaryCashflowCalculationId,80)))throw new TenantBoundaryException("AI_SUMMARY_INVALID",400);
        var basis = summary ? "summary" : input.DocumentVersionId is not null ? "document" : input.NetWorthPositionId is not null ? "net-worth" : "financial";
        var customer = await owners.Authorize(http);
        var settings = await owners.Configuration(http);
        if (!settings.Available) throw new TenantBoundaryException("AI_NOT_CONFIGURED", 503);
        if (settings.Revision != input.ConfigurationRevision) throw new TenantBoundaryException("AI_PERMISSION_CHANGED", 409);
        var requestHash = QaStore.Hash(JsonSerializer.Serialize(input, Json));
        return await store.ExecuteAsync(async records => {
            var scope = records.Where(x => x.TenantId == customer.Session.TenantId && x.CustomerId == customer.Workspace.CustomerId && x.ActorId == customer.Session.ActorId).ToArray();
            var previous = scope.SingleOrDefault(x => x.Answer.OperationId == input.OperationId);
            if (previous is not null)
            {
                if (previous.RequestHash != requestHash) throw new TenantBoundaryException("AI_OPERATION_CONFLICT", 409);
                await owners.Validate(http, customer, previous.Answer.Claims);
                CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
                return previous.Answer;
            }
            if (scope.Length >= 1000 || records.Count >= 10000) throw new TenantBoundaryException("AI_HISTORY_CAPACITY", 409);
            if (scope.Count(x => x.Answer.RecordedAt > DateTimeOffset.UtcNow.AddMinutes(-1)) >= 5)
                throw new TenantBoundaryException("AI_RATE_LIMITED", 429);
            // Reserve durably before external spend. Failed/interrupted attempts are replayed
            // as NOT_COMPLETED; a new explicit submission is required to spend again.
            var attempt = new QaAnswer(Guid.NewGuid().ToString("N"), input.OperationId, input.Question, "NOT_COMPLETED",
                "This attempt did not complete. No financial answer was produced. You may submit a new question to retry.",
                [], DateTimeOffset.UtcNow, "", PromptVersion, settings.Revision);
            var recordIndex = records.Count;
            records.Add(new(customer.Session.TenantId, customer.Workspace.CustomerId, customer.Session.ActorId, requestHash,
                attempt, [], [], "", "", "", true, settings.Notice, input.DocumentVersionId, input.NetWorthPositionId));
            await store.CheckpointAsync(records);
            if (FinancialQaSafety.ContainsInstructionOverride(input.Question))
            {
                CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
                if ((await owners.Configuration(http)).Revision != settings.Revision) throw new TenantBoundaryException("AI_PERMISSION_CHANGED", 409);
                var blocked = new QaAnswer(attempt.AnswerId, input.OperationId, input.Question, "UNSUPPORTED",
                    "This request was not answered because the question contained instruction-like content that cannot be used as a financial question. Ask a factual question about the authorized records instead.",
                    [], DateTimeOffset.UtcNow, "", PromptVersion, settings.Revision);
                records[recordIndex] = new(customer.Session.TenantId, customer.Workspace.CustomerId, customer.Session.ActorId, requestHash, blocked,
                    [], [], "", "", "", true, settings.Notice, input.DocumentVersionId, input.NetWorthPositionId);
                return blocked;
            }
            var planResult = summary ? new QaModelResult("", "{\"mode\":\"lookup\",\"query\":\"reviewed financial summary\"}") : await owners.Generate(http, new("plan", input.Question, settings.Revision, true, [], basis));
            var plan = ParsePlan(planResult.Content); var searchIds = new List<string>();
            IReadOnlyList<QaClaim> retrieved = []; IReadOnlyList<QaClaim> selected = [];
            QaModelResult? selection = null;
            if (plan.Mode == "calculation" && basis != "net-worth") throw Invalid();
            if (plan.Mode is "lookup" or "calculation")
            {
                retrieved = basis switch {
                    "summary" => await owners.Summary(http,customer,input.SummaryNetWorthCalculationId!,input.SummaryCashflowCalculationId!),
                    "document" => await owners.Document(http, customer, input.DocumentVersionId!),
                    "net-worth" => await owners.Calculate(http, customer, input.NetWorthPositionId!, input.OperationId),
                    _ => await owners.Retrieve(http, customer, plan.Query, searchIds)
                };
                if (retrieved.Any(claim => FinancialQaSafety.ContainsInstructionOverride(claim.Text)))
                {
                    await owners.Validate(http, customer, retrieved);
                    CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
                    if ((await owners.Configuration(http)).Revision != settings.Revision) throw new TenantBoundaryException("AI_PERMISSION_CHANGED", 409);
                    var blocked = new QaAnswer(attempt.AnswerId, input.OperationId, input.Question, "UNSUPPORTED",
                        "This request was not answered because the selected source contained instruction-like content that cannot be used as financial evidence. Review the source and ask a factual question about authorized records.",
                        [], DateTimeOffset.UtcNow, planResult.Model, PromptVersion, settings.Revision);
                    records[recordIndex] = new(customer.Session.TenantId, customer.Workspace.CustomerId, customer.Session.ActorId, requestHash, blocked,
                        searchIds, [], planResult.Content, "", planResult.Model, true, settings.Notice, input.DocumentVersionId, input.NetWorthPositionId);
                    return blocked;
                }
                records[recordIndex] = records[recordIndex] with { SearchRecordIds = searchIds, RetrievedClaims = retrieved, PlanJson = planResult.Content, PlanModel = planResult.Model };
                await store.CheckpointAsync(records);
                if (summary && retrieved.Any(x => x.Id.EndsWith("-incomplete",StringComparison.Ordinal))) selected = retrieved;
                else if (retrieved.Count > 0)
                {
                    CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
                    selection = await owners.Generate(http, new("select", input.Question, settings.Revision, true, retrieved, basis));
                    selected = Select(selection.Content, retrieved);
                    if(summary && selected.Count>0 && (retrieved.Count!=6 || selected.Count!=retrieved.Count))throw Invalid();
                }
            }
            await owners.Validate(http, customer, retrieved);
            CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
            if ((await owners.Configuration(http)).Revision != settings.Revision) throw new TenantBoundaryException("AI_PERMISSION_CHANGED", 409);
            var state = plan.Mode == "unsupported" ? "UNSUPPORTED" : selected.Count == 0 || selected.Any(x => x.Id.EndsWith("-incomplete",StringComparison.Ordinal)) ? "INSUFFICIENT_DATA" : "SOURCES";
            var answer = new QaAnswer(attempt.AnswerId, input.OperationId, input.Question, state,
                state == "SOURCES" && summary ? "Financial summary of your selected customer-reviewed inventories: assets, liabilities and net worth at month end, plus income, expenses and balance for that completed month. Financial Rules calculated every amount; the model only organized these cited results. These are self-declared, not externally verified. Monthly balance is not a bank balance or a change in net worth."
                : state == "SOURCES" && basis == "document" ? "These passages come from the selected document version. Text and OCR transcriptions may contain errors; verify the original. They are not authoritative financial facts."
                : state == "SOURCES" && basis == "net-worth" ? "Financial Rules calculated these values from your selected reviewed inventory. The model only selected relevant results; it did not calculate figures."
                : state == "SOURCES" ? "These matching source records may help. This is a limited retrieval, not a complete financial inventory or verification of the figures."
                : state == "UNSUPPORTED" ? "This question is outside the selected source scope. Choose a reviewed Net worth position for its supported calculation; advice, forecasts and external verification are not available here."
                : "There is insufficient authorized source information to answer this question. Review the selected source and any missing calculation inputs. Process the selected scan first if OCR is not recorded. Unsupported, oversized or uncertain document text is not used.",
                selected, DateTimeOffset.UtcNow, selection?.Model ?? planResult.Model, PromptVersion, settings.Revision);
            records[recordIndex] = new(customer.Session.TenantId, customer.Workspace.CustomerId, customer.Session.ActorId, requestHash, answer,
                searchIds, retrieved, planResult.Content, selection?.Content ?? "", planResult.Model, true, settings.Notice, input.DocumentVersionId, input.NetWorthPositionId);
            return answer;
        }, http.RequestAborted);
    }
    public async Task<IReadOnlyList<QaAnswer>> History(HttpContext http)
    {
        var customer = await owners.Authorize(http);
        return await store.ExecuteAsync(async records => {
            var matches = records.Where(x => x.TenantId == customer.Session.TenantId && x.CustomerId == customer.Workspace.CustomerId && x.ActorId == customer.Session.ActorId)
                .OrderByDescending(x => x.Answer.RecordedAt).Take(20).Select(x => x.Answer).ToArray();
            foreach (var answer in matches) await owners.Validate(http, customer, answer.Claims);
            CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
            return (IReadOnlyList<QaAnswer>)matches;
        }, http.RequestAborted);
    }
    private static TenantBoundaryException Invalid() => new("AI_UNGROUNDED_RESPONSE", 503);
}
