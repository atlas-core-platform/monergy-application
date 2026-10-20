using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductGoalContributionList(IReadOnlyList<ReportedGoalContribution> Items, bool HasMore, string DataDurability);
public static class ProductGoalContributionEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductGoalContributions(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductGoalContributionClient>();
    }
    public static void MapProductGoalContributions(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/goal-contributions");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "GOAL_CONTRIBUTION_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "GOAL_CONTRIBUTION_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "GOAL_CONTRIBUTION_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (string financialGoalId,HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductGoalContributionClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId,financialGoalId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items is null || list.Items.Count > 200 || list.HasMore && list.Items.Count != 200 || list.Items.Select(x => x?.GoalContributionId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count || list.Items.Any(item => !Bound(item, customer.CustomerId)||item.Contribution.FinancialGoalId!=financialGoalId))
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductGoalContributionList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductGoalContributionClient client,ProductFinancialGoalClient goals) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync<CreateGoalContribution>(http).ConfigureAwait(false);
            if (input.Contribution is null) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var source=await GoalAsync(goals,session,customer.CustomerId,input.Contribution,http.RequestAborted).ConfigureAwait(false);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != 1 || receipt.DeclarationStatus!="CUSTOMER_REPORTED" ||
                receipt.Contribution != input.Contribution || receipt.SourceGoal!=source.Goal)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.GoalContributionId, http.RequestAborted,1).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapGet("/{goalContributionId}", async (string goalContributionId,int? revision,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductGoalContributionClient client) =>
        {
            var (session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            if(!TenantAccessClient.Identifier(goalContributionId,80)||revision is < 1) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var record=await client.ReadAsync(session,customer.CustomerId,goalContributionId,http.RequestAborted,revision).ConfigureAwait(false);
            if(!Bound(record,customer.CustomerId)||record.GoalContributionId!=goalContributionId||revision is not null && record.Revision!=revision) throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);
            return record;
        });
        group.MapGet("/{goalContributionId}/history", async (string goalContributionId, int? beforeRevision, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductGoalContributionClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(goalContributionId,80) || beforeRevision is < 1) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, goalContributionId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.GoalContributionId != goalContributionId || original.Revision != 1)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            var history = await client.HistoryAsync(session, customer.CustomerId, goalContributionId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
            if (history.Items is null || history.Items.Count > 50 || history.Items.Any(x => !Bound(x,customer.CustomerId) || x.GoalContributionId != goalContributionId || beforeRevision is not null && x.Revision >= beforeRevision ||
                 x.SourceGoal!=original.SourceGoal || x.Contribution.FinancialGoalId!=original.Contribution.FinancialGoalId || x.Contribution.GoalRevision!=original.Contribution.GoalRevision) ||
                history.Items.Zip(history.Items.Skip(1)).Any(pair => pair.First.Revision != pair.Second.Revision + 1) ||
                history.Items.Where(x=>x.DeclarationStatus=="CUSTOMER_WITHDRAWN").Any(x=>beforeRevision is not null || x!=history.Items[0] || history.Items.Count<2 || x.Contribution!=history.Items[1].Contribution) ||
                history.HasMore && (history.Items.Count != 50 || history.NextBeforeRevision != history.Items[^1].Revision) || !history.HasMore && (history.NextBeforeRevision is not null || history.Items.Count > 0 && history.Items[^1].Revision != 1) ||
                history.Items.Count == 0 && beforeRevision != 1)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return history;
        });
        group.MapPost("/{goalContributionId}/withdrawals",async(string goalContributionId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductGoalContributionClient client)=>
        {
            var (session,customer)=await ResolveAsync(http,owners,access,true).ConfigureAwait(false);
            if(!TenantAccessClient.Identifier(goalContributionId,80))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var input=await ReadInputAsync<WithdrawGoalContribution>(http).ConfigureAwait(false);
            if(!ValidWithdrawal(input))throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var prior=await client.ReadAsync(session,customer.CustomerId,goalContributionId,http.RequestAborted,input.ExpectedRevision).ConfigureAwait(false);
            if(!Bound(prior,customer.CustomerId)||prior.GoalContributionId!=goalContributionId||prior.Revision!=input.ExpectedRevision)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            if(prior.DeclarationStatus!="CUSTOMER_REPORTED")throw new TenantBoundaryException("GOAL_CONTRIBUTION_WITHDRAWN",409);
            var receipt=await client.WithdrawAsync(session,customer.CustomerId,goalContributionId,input,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(receipt,customer.CustomerId)||receipt!= (prior with {OperationId=input.OperationId,RecordedBy=session.ActorId,RecordedAt=receipt.RecordedAt,
                Revision=input.ExpectedRevision+1,DeclarationStatus="CUSTOMER_WITHDRAWN",WithdrawalReason=input.Reason}))
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            var saved=await client.ReadAsync(session,customer.CustomerId,goalContributionId,http.RequestAborted,receipt.Revision).ConfigureAwait(false);
            if(saved!=receipt)throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            await ConfirmAsync(http,owners,access,session,customer,true).ConfigureAwait(false);
            return saved;
        });
        group.MapPost("/{goalContributionId}/corrections", async (string goalContributionId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductGoalContributionClient client,ProductFinancialGoalClient goals) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(goalContributionId,80)) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var input = await ReadInputAsync<ReviseGoalContribution>(http).ConfigureAwait(false);
            if (input.Contribution is null || input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, goalContributionId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || original.GoalContributionId != goalContributionId)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            if(input.Contribution.FinancialGoalId!=original.Contribution.FinancialGoalId||input.Contribution.GoalRevision!=original.Contribution.GoalRevision)throw new TenantBoundaryException("GOAL_CONTRIBUTION_SOURCE_IMMUTABLE",400);
            var source=await GoalAsync(goals,session,customer.CustomerId,input.Contribution,http.RequestAborted).ConfigureAwait(false);
            var receipt = await client.ReviseAsync(session, customer.CustomerId, goalContributionId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || !Bound(receipt,customer.CustomerId) || original.GoalContributionId != goalContributionId || receipt.GoalContributionId != goalContributionId ||
                receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 ||
                receipt.Contribution != input.Contribution ||
                receipt.DeclarationStatus != original.DeclarationStatus || receipt.SourceGoal!=original.SourceGoal || receipt.SourceGoal!=source.Goal)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            var saved = await client.ReadAsync(session, customer.CustomerId, goalContributionId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
    }
    private static async Task<T> ReadInputAsync<T>(HttpContext http) where T : class
    {
        try
        {
            http.Request.EnableBuffering(32768,32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            return json.RootElement.Deserialize<T>(InputJson)
                ?? throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            { if(!names.Add(property.Name)) throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400); RejectDuplicates(property.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool ValidWithdrawal(WithdrawGoalContribution input)=>Guid.TryParseExact(input.OperationId,"N",out _)&&!input.OperationId.Any(char.IsAsciiLetterUpper)&&input.ExpectedRevision is >0 and <int.MaxValue&&!string.IsNullOrWhiteSpace(input.Reason)&&input.Reason.Length<=500&&input.Reason==input.Reason.Trim()&&!input.Reason.Any(char.IsControl);
    private static bool Bound(ReportedGoalContribution? value,string customer)=>value is {Contribution:not null,SourceGoal:not null}&&value.CustomerId==customer&&
        value.Revision>0&&TenantAccessClient.Identifier(value.GoalContributionId,80)&&Guid.TryParseExact(value.OperationId,"N",out _)&&!value.OperationId.Any(char.IsAsciiLetterUpper)&&
        TenantAccessClient.Identifier(value.Contribution.FinancialGoalId,80)&&value.Contribution.GoalRevision>0&&value.Contribution.Amount is >0 and <=999999999999.99m&&decimal.Round(value.Contribution.Amount,2)==value.Contribution.Amount&&
        value.Contribution.ContributionDate.Year>=1900&&value.Contribution.ContributionDate<=DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14))&&
        !string.IsNullOrWhiteSpace(value.SourceGoal.Name)&&value.SourceGoal.Name.Trim().Length<=120&&!value.SourceGoal.Name.Any(char.IsControl)&&
        value.SourceGoal.Currency is {Length:3}&&value.SourceGoal.Currency.All(char.IsAsciiLetterUpper)&&value.SourceGoal.TargetAmount is >0 and <=999999999999.99m&&decimal.Round(value.SourceGoal.TargetAmount,2)==value.SourceGoal.TargetAmount&&value.SourceGoal.TargetDate.Year is >=1900 and <=2199&&
        (value.DeclarationStatus=="CUSTOMER_REPORTED"&&value.WithdrawalReason is null||value.DeclarationStatus=="CUSTOMER_WITHDRAWN"&&value.Revision>1&&ValidWithdrawal(new(value.OperationId,value.Revision-1,value.WithdrawalReason!)))&&TenantAccessClient.Identifier(value.RecordedBy,160)&&value.RecordedAt!=default;
    private static async Task<CustomerFinancialGoal> GoalAsync(ProductFinancialGoalClient goals,CustomerOwnerSession session,string customer,GoalContributionDraft input,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(input.FinancialGoalId,80)||input.GoalRevision<1)throw new TenantBoundaryException("GOAL_CONTRIBUTION_INVALID",400);
        var source=await goals.ReadAsync(session,customer,input.FinancialGoalId,ct,input.GoalRevision).ConfigureAwait(false);
        if(source is not {Goal:not null,DeclarationStatus:"CUSTOMER_DEFINED"}||source.CustomerId!=customer||source.FinancialGoalId!=input.FinancialGoalId||source.Revision!=input.GoalRevision||!Guid.TryParseExact(source.OperationId,"N",out _)||!TenantAccessClient.Identifier(source.RecordedBy,160)||source.RecordedAt==default)
            throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID",503);
        return source;
    }
    private static async Task<(CustomerOwnerSession Session, CustomerOwnerWorkspace Customer)> ResolveAsync(
        HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, bool write)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
        var read = await access.EvaluateCustomerAsync(http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
        if (read.Outcome != "Allow" || read.AccessBasis != "Self" || read.ActorId != session.ActorId ||
            read.SubjectVersion != session.SubjectVersion)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        if (write)
        {
            var update = await access.EvaluateCustomerAsync(http, "financial-profile.profile.update", customer.CustomerId).ConfigureAwait(false);
            if (update.Outcome != "Allow" || update.AccessBasis != "Self" || update.ActorId != session.ActorId ||
                update.SubjectVersion != read.SubjectVersion || update.CustomerVersion != read.CustomerVersion)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
        }
        return (session, customer);
    }
    private static async Task ConfirmAsync(HttpContext http, CustomerOwnerClient owners, TenantAccessClient access,
        CustomerOwnerSession original, CustomerOwnerWorkspace originalCustomer, bool write)
    {
        var (current, customer) = await ResolveAsync(http, owners, access, write).ConfigureAwait(false);
        if (current.TenantId != original.TenantId || current.ActorId != original.ActorId ||
            current.SubjectVersion != original.SubjectVersion || customer.CustomerId != originalCustomer.CustomerId || customer.Version != originalCustomer.Version)
            throw new TenantBoundaryException("GOAL_CONTRIBUTION_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductGoalContributionClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductGoalContributionClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductGoalContributionClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<GoalContributionList> ListAsync(CustomerOwnerSession session, string customer,string financialGoalId, CancellationToken ct) =>
        SendAsync<GoalContributionList>(session, customer, "?financialGoalId="+Uri.EscapeDataString(financialGoalId), null, ct);
    public Task<ReportedGoalContribution> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct, int? revision = null) =>
        SendAsync<ReportedGoalContribution>(session, customer, "/" + Uri.EscapeDataString(id) + (revision is null ? "" : "?revision=" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<ReportedGoalContribution> CreateAsync(CustomerOwnerSession session, string customer, CreateGoalContribution input, CancellationToken ct) =>
        SendAsync<ReportedGoalContribution>(session, customer, "", input, ct);
    public Task<ReportedGoalContribution> ReviseAsync(CustomerOwnerSession session, string customer, string id, ReviseGoalContribution input, CancellationToken ct) =>
        SendAsync<ReportedGoalContribution>(session, customer, "/" + Uri.EscapeDataString(id) + "/corrections", input, ct);
    public Task<ReportedGoalContribution> WithdrawAsync(CustomerOwnerSession session,string customer,string id,WithdrawGoalContribution input,CancellationToken ct)=>
        SendAsync<ReportedGoalContribution>(session,customer,"/"+Uri.EscapeDataString(id)+"/withdrawals",input,ct);
    public Task<GoalContributionHistory> HistoryAsync(CustomerOwnerSession session, string customer, string id, int? beforeRevision, CancellationToken ct) =>
        SendAsync<GoalContributionHistory>(session, customer, "/" + Uri.EscapeDataString(id) + "/history" + (beforeRevision is null ? "" : "?beforeRevision=" + beforeRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, object? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/goal-contributions" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "GOAL_CONTRIBUTION_NOT_FOUND",
                409 => "GOAL_CONTRIBUTION_OPERATION_CONFLICT", 400 => "GOAL_CONTRIBUTION_INVALID", 413 => "GOAL_CONTRIBUTION_TOO_LARGE", _ => "GOAL_CONTRIBUTION_UNAVAILABLE" };
            if (status == 409)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value) && value.GetString()=="GOAL_CONTRIBUTION_REVISION_CONFLICT") code="GOAL_CONTRIBUTION_REVISION_CONFLICT";
            }
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("GOAL_CONTRIBUTION_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
