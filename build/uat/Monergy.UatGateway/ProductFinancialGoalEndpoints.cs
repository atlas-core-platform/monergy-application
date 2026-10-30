using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductFinancialGoalList(IReadOnlyList<CustomerFinancialGoal> Items, bool HasMore, string DataDurability);
public static class ProductFinancialGoalEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductFinancialGoals(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductFinancialGoalClient>();
    }
    public static void MapProductFinancialGoals(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/financial-goals");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "FINANCIAL_GOAL_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "FINANCIAL_GOAL_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "FINANCIAL_GOAL_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductFinancialGoalClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items is null || list.Items.Count > 200 || list.HasMore && list.Items.Count != 200 || list.Items.Select(x => x?.FinancialGoalId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count || list.Items.Any(item => !Bound(item, customer.CustomerId)))
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductFinancialGoalList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductFinancialGoalClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync<CreateFinancialGoal>(http).ConfigureAwait(false);
            if (input.Goal is null) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != 1 ||
                receipt.Goal != input.Goal with { Name = input.Goal.Name.Trim() })
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.FinancialGoalId, http.RequestAborted,1).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapGet("/{financialGoalId}", async (string financialGoalId,int? revision,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductFinancialGoalClient client) =>
        {
            var (session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            if(!TenantAccessClient.Identifier(financialGoalId,80)||revision is < 1) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
            var record=await client.ReadAsync(session,customer.CustomerId,financialGoalId,http.RequestAborted,revision).ConfigureAwait(false);
            if(!Bound(record,customer.CustomerId)||record.FinancialGoalId!=financialGoalId||revision is not null && record.Revision!=revision) throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);
            return record;
        });
        group.MapGet("/{financialGoalId}/progress", async (string financialGoalId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductFinancialGoalClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(financialGoalId, 80)) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);
            var progress = await client.ProgressAsync(session, customer.CustomerId, financialGoalId, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(progress, customer.CustomerId, financialGoalId))
                throw new TenantBoundaryException("GOAL_PROGRESS_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return progress;
        });
        group.MapGet("/{financialGoalId}/history", async (string financialGoalId, int? beforeRevision, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductFinancialGoalClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(financialGoalId,80) || beforeRevision is < 1) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, financialGoalId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.FinancialGoalId != financialGoalId || original.Revision != 1)
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
            var history = await client.HistoryAsync(session, customer.CustomerId, financialGoalId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
            if (history.Items is null || history.Items.Count > 50 || history.Items.Any(x => !Bound(x,customer.CustomerId) || x.FinancialGoalId != financialGoalId || beforeRevision is not null && x.Revision >= beforeRevision ||
                x.DeclarationStatus != original.DeclarationStatus) ||
                history.Items.Zip(history.Items.Skip(1)).Any(pair => pair.First.Revision != pair.Second.Revision + 1) ||
                history.HasMore && (history.Items.Count != 50 || history.NextBeforeRevision != history.Items[^1].Revision) || !history.HasMore && (history.NextBeforeRevision is not null || history.Items.Count > 0 && history.Items[^1].Revision != 1) ||
                history.Items.Count == 0 && beforeRevision != 1)
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return history;
        });
        group.MapPost("/{financialGoalId}/corrections", async (string financialGoalId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductFinancialGoalClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(financialGoalId,80)) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
            var input = await ReadInputAsync<ReviseFinancialGoal>(http).ConfigureAwait(false);
            if (input.Goal is null || input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, financialGoalId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || original.FinancialGoalId != financialGoalId)
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
            var receipt = await client.ReviseAsync(session, customer.CustomerId, financialGoalId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || !Bound(receipt,customer.CustomerId) || original.FinancialGoalId != financialGoalId || receipt.FinancialGoalId != financialGoalId ||
                receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 ||
                receipt.Goal != input.Goal with { Name = input.Goal.Name.Trim() } ||
                receipt.DeclarationStatus != original.DeclarationStatus)
                throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
            var saved = await client.ReadAsync(session, customer.CustomerId, financialGoalId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID",503);
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
                ?? throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            { if(!names.Add(property.Name)) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID",400); RejectDuplicates(property.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool Bound(CustomerFinancialGoal? value, string customer) => value is { Goal: not null } && value.CustomerId == customer &&
        value.Revision > 0 && TenantAccessClient.Identifier(value.FinancialGoalId,80) && Guid.TryParseExact(value.OperationId,"N",out _) &&
        !value.OperationId.Any(char.IsAsciiLetterUpper) && !string.IsNullOrWhiteSpace(value.Goal.Name) && value.Goal.Name.Trim().Length <= 120 && !value.Goal.Name.Any(char.IsControl) &&
        value.Goal.TargetAmount is > 0 and <= 999999999999.99m && decimal.Round(value.Goal.TargetAmount,2)==value.Goal.TargetAmount &&
        value.Goal.Currency is { Length:3 } && value.Goal.Currency.All(char.IsAsciiLetterUpper) && value.Goal.TargetDate.Year is >= 1900 and <= 2199 &&
        value.DeclarationStatus == "CUSTOMER_DEFINED" && TenantAccessClient.Identifier(value.RecordedBy,160) && value.RecordedAt != default;
    private static bool Bound(FinancialGoalProgress? value, string customer, string financialGoalId)
    {
        if (value is not { Goal: not null, Sources: not null } || value.CustomerId != customer || value.FinancialGoalId != financialGoalId ||
            value.GoalRevision < 1 || string.IsNullOrWhiteSpace(value.Goal.Name) || value.Goal.Name != value.Goal.Name.Trim() || value.Goal.Name.Length > 120 || value.Goal.Name.Any(char.IsControl) ||
            value.Goal.TargetAmount is <= 0 or > 999999999999.99m || decimal.Round(value.Goal.TargetAmount, 2) != value.Goal.TargetAmount ||
            value.Goal.Currency is not { Length: 3 } || value.Goal.Currency.Any(character => !char.IsAsciiLetterUpper(character)) ||
            value.Goal.TargetDate.Year is < 1900 or > 2199 || value.IncludedContributionCount != value.Sources.Count || value.WithdrawnContributionCount < 0 || value.Sources.Count + value.WithdrawnContributionCount > 200 ||
            value.ProgressBasis != "CUSTOMER_REPORTED" || value.AchievementStatus != "NOT_DETERMINED" || value.ObservedAt == default ||
            value.Sources.Select(source => source.GoalContributionId).Distinct(StringComparer.Ordinal).Count() != value.Sources.Count ||
            value.Sources.Any(source => !TenantAccessClient.Identifier(source.GoalContributionId, 80) || source.ContributionRevision < 1 || source.GoalRevision < 1 ||
                source.Amount is <= 0 or > 999999999999.99m || decimal.Round(source.Amount, 2) != source.Amount || source.ContributionDate.Year is < 1900 or > 2199)) return false;
        decimal total;
        try { total = value.Sources.Aggregate(0m, static (sum, source) => checked(sum + source.Amount)); }
        catch (OverflowException) { return false; }
        total = decimal.Round(total, 2, MidpointRounding.AwayFromZero);
        var remaining = decimal.Round(decimal.Max(0m, value.Goal.TargetAmount - total), 2, MidpointRounding.AwayFromZero);
        var percent = decimal.Round(total / value.Goal.TargetAmount * 100m, 2, MidpointRounding.AwayFromZero);
        DateOnly? latest = value.Sources.Count == 0 ? null : value.Sources.Max(source => source.ContributionDate);
        return value.ReportedContributionAmount == total && value.RemainingAmount == remaining && value.ProgressPercent == percent && value.LatestContributionDate == latest;
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
            throw new TenantBoundaryException("FINANCIAL_GOAL_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductFinancialGoalClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductFinancialGoalClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductFinancialGoalClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<FinancialGoalList> ListAsync(CustomerOwnerSession session, string customer, CancellationToken ct) =>
        SendAsync<FinancialGoalList>(session, customer, "", null, ct);
    public Task<CustomerFinancialGoal> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct, int? revision = null) =>
        SendAsync<CustomerFinancialGoal>(session, customer, "/" + Uri.EscapeDataString(id) + (revision is null ? "" : "?revision=" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<CustomerFinancialGoal> CreateAsync(CustomerOwnerSession session, string customer, CreateFinancialGoal input, CancellationToken ct) =>
        SendAsync<CustomerFinancialGoal>(session, customer, "", input, ct);
    public Task<CustomerFinancialGoal> ReviseAsync(CustomerOwnerSession session, string customer, string id, ReviseFinancialGoal input, CancellationToken ct) =>
        SendAsync<CustomerFinancialGoal>(session, customer, "/" + Uri.EscapeDataString(id) + "/corrections", input, ct);
    public Task<FinancialGoalHistory> HistoryAsync(CustomerOwnerSession session, string customer, string id, int? beforeRevision, CancellationToken ct) =>
        SendAsync<FinancialGoalHistory>(session, customer, "/" + Uri.EscapeDataString(id) + "/history" + (beforeRevision is null ? "" : "?beforeRevision=" + beforeRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<FinancialGoalProgress> ProgressAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct) =>
        SendAsync<FinancialGoalProgress>(session, customer, "/" + Uri.EscapeDataString(id) + "/progress", null, ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, object? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/financial-goals" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "FINANCIAL_GOAL_NOT_FOUND",
                409 => "FINANCIAL_GOAL_OPERATION_CONFLICT", 400 => "FINANCIAL_GOAL_INVALID", 413 => "FINANCIAL_GOAL_TOO_LARGE", _ => "FINANCIAL_GOAL_UNAVAILABLE" };
            if (status == 409)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value) && value.GetString()=="FINANCIAL_GOAL_REVISION_CONFLICT") code="FINANCIAL_GOAL_REVISION_CONFLICT";
            }
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("FINANCIAL_GOAL_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
