using System.Text.Json;
using System.Text.Json.Serialization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.AiIntelligence.Qa;

public sealed class QaApplication(IQaOwners owners, QaStore store)
{
    public const string PromptVersion = "monergy-basic-qa/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static QaPlan ParsePlan(string content)
    {
        var plan = Parse<QaPlan>(content);
        if (plan.Mode is not ("lookup" or "unsupported") || !NetWorthData.Text(plan.Query, 256)) throw Invalid();
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
                attempt, [], [], "", "", "", true, settings.Notice));
            await store.CheckpointAsync(records);
            var planResult = await owners.Generate(http, new("plan", input.Question, settings.Revision, true, []));
            var plan = ParsePlan(planResult.Content); var searchIds = new List<string>();
            IReadOnlyList<QaClaim> retrieved = []; IReadOnlyList<QaClaim> selected = [];
            QaModelResult? selection = null;
            if (plan.Mode == "lookup")
            {
                retrieved = await owners.Retrieve(http, customer, plan.Query, searchIds);
                if (retrieved.Count > 0)
                {
                    CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
                    selection = await owners.Generate(http, new("select", input.Question, settings.Revision, true, retrieved));
                    selected = Select(selection.Content, retrieved);
                }
            }
            await owners.Validate(http, customer, retrieved);
            CustomerReviewHttp.Confirm(customer.Decision, (await owners.Authorize(http)).Decision);
            if ((await owners.Configuration(http)).Revision != settings.Revision) throw new TenantBoundaryException("AI_PERMISSION_CHANGED", 409);
            var state = plan.Mode == "unsupported" ? "UNSUPPORTED" : selected.Count == 0 ? "INSUFFICIENT_DATA" : "SOURCES";
            var answer = new QaAnswer(attempt.AnswerId, input.OperationId, input.Question, state,
                state == "SOURCES" ? "These matching source records may help. This is a limited retrieval, not a complete financial inventory or verification of the figures."
                : state == "UNSUPPORTED" ? "This question needs a calculation, verified information or advice outside basic source Q&A. Use the Net worth journey for its supported deterministic calculation."
                : "There is insufficient authorized source information to answer this question. Try a specific source label or add and process the relevant document.",
                selected, DateTimeOffset.UtcNow, selection?.Model ?? planResult.Model, PromptVersion, settings.Revision);
            records[recordIndex] = new(customer.Session.TenantId, customer.Workspace.CustomerId, customer.Session.ActorId, requestHash, answer,
                searchIds, retrieved, planResult.Content, selection?.Content ?? "", planResult.Model, true, settings.Notice);
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
