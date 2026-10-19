using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductLiabilityRecordList(IReadOnlyList<ReviewedLiabilityRecord> Items, bool HasMore, string DataDurability);
public static class ProductLiabilityRecordEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductLiabilityRecords(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductLiabilityRecordClient>();
    }
    public static void MapProductLiabilityRecords(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/liability-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "LIABILITY_RECORD_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "LIABILITY_RECORD_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "LIABILITY_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductLiabilityRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items.Count > 200 || list.Items.Any(item => !Bound(item, customer.CustomerId)))
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductLiabilityRecordList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapGet("/snapshot",async (HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductLiabilityRecordClient client)=>
        {
            var(session,customer)=await ResolveAsync(http,owners,access,false).ConfigureAwait(false);
            var(date,currency)=SnapshotSelection(http.Request);
            var value=await client.SnapshotAsync(session,customer.CustomerId,date,currency,http.RequestAborted).ConfigureAwait(false);
            if(value.AsOfDate!=date || value.Currency!=currency || value.Items.Count>200 || value.HasMore && value.Items.Count!=200 ||
                value.Items.Select(x=>x?.LiabilityRecordId).Distinct(StringComparer.Ordinal).Count()!=value.Items.Count ||
                value.Items.Any(x=>!Bound(x,customer.CustomerId) || x.Liability.BalanceAsOfDate!=date || x.Currency!=currency))
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http,owners,access,session,customer,false).ConfigureAwait(false);
            return value;
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductLiabilityRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync<CreateLiabilityRecord>(http).ConfigureAwait(false);
            if (input.Liability is null) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId ||
                receipt.Liability != input.Liability with { Name = input.Liability.Name.Trim(), Creditor = input.Liability.Creditor.Trim() })
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.LiabilityRecordId, http.RequestAborted,1).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapGet("/{liabilityRecordId}/history", async (string liabilityRecordId, int? beforeRevision, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductLiabilityRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(liabilityRecordId,80) || beforeRevision is < 1) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400);
            var history = await client.HistoryAsync(session, customer.CustomerId, liabilityRecordId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
            if (history.Items.Count > 50 || history.Items.Any(x => !Bound(x,customer.CustomerId) || x.LiabilityRecordId != liabilityRecordId || beforeRevision is not null && x.Revision >= beforeRevision) ||
                history.Items.Zip(history.Items.Skip(1)).Any(pair => pair.First.Revision != pair.Second.Revision + 1) ||
                history.HasMore && (history.Items.Count != 50 || history.NextBeforeRevision != history.Items[^1].Revision) || !history.HasMore && history.NextBeforeRevision is not null)
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return history;
        });
        group.MapPost("/{liabilityRecordId}/corrections", async (string liabilityRecordId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductLiabilityRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(liabilityRecordId,80)) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400);
            var input = await ReadInputAsync<ReviseLiabilityRecord>(http).ConfigureAwait(false);
            if (input.Liability is null || input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, liabilityRecordId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || original.LiabilityRecordId != liabilityRecordId)
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID",503);
            var receipt = await client.ReviseAsync(session, customer.CustomerId, liabilityRecordId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || !Bound(receipt,customer.CustomerId) || original.LiabilityRecordId != liabilityRecordId || receipt.LiabilityRecordId != liabilityRecordId ||
                receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 ||
                receipt.Liability != input.Liability with { Name = input.Liability.Name.Trim(), Creditor = input.Liability.Creditor.Trim() } ||
                receipt.VerificationStatus != original.VerificationStatus || receipt.Currency != original.Currency || receipt.SourceProvenanceId != original.SourceProvenanceId || receipt.DocumentVersionId != original.DocumentVersionId || receipt.EvidenceId != original.EvidenceId ||
                receipt.Liability.SourceFinancialFactId != original.Liability.SourceFinancialFactId || receipt.Liability.SourceRevision != original.Liability.SourceRevision)
                throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID",503);
            var saved = await client.ReadAsync(session, customer.CustomerId, liabilityRecordId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
    }
    private static (DateOnly Date,string Currency) SnapshotSelection(HttpRequest request)
    {
        if (request.Query.Count!=2 || request.Query.Keys.Any(key=>key is not ("asOfDate" or "currency")) ||
            request.Query["asOfDate"].Count!=1 || request.Query["currency"].Count!=1 ||
            !DateOnly.TryParseExact(request.Query["asOfDate"].ToString(),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var date) ||
            date.Year<1900 || date>DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)))
            throw new TenantBoundaryException("LIABILITY_SNAPSHOT_INVALID",400);
        var currency=request.Query["currency"].ToString();
        if(currency.Length!=3 || !currency.All(char.IsAsciiLetterUpper)) throw new TenantBoundaryException("LIABILITY_SNAPSHOT_INVALID",400);
        return(date,currency);
    }
    private static async Task<T> ReadInputAsync<T>(HttpContext http) where T : class
    {
        try
        {
            http.Request.EnableBuffering(32768,32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            return json.RootElement.Deserialize<T>(InputJson)
                ?? throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            { if(!names.Add(property.Name)) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID",400); RejectDuplicates(property.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool Bound(ReviewedLiabilityRecord? value, string customer) => value is { Liability: not null, Currency: not null, SourceProvenanceId: not null, DocumentVersionId: not null } && value.CustomerId == customer &&
        value.Revision > 0 && TenantAccessClient.Identifier(value.LiabilityRecordId, 80) && Guid.TryParseExact(value.OperationId, "N", out _) &&
        value.Currency.Length == 3 && value.Currency.All(char.IsAsciiLetterUpper) &&
        Text(value.Liability.Name) && Text(value.Liability.Creditor) &&
        Money(value.Liability.OutstandingAmount) && value.VerificationStatus == "CUSTOMER_REVIEWED" &&
        value.Liability.BalanceAsOfDate.Year >= 1900 &&
        value.Liability.BalanceAsOfDate <= DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)) &&
        TenantAccessClient.Identifier(value.Liability.SourceFinancialFactId,160) && value.Liability.SourceRevision > 0 &&
        TenantAccessClient.Identifier(value.SourceProvenanceId,160) && TenantAccessClient.Identifier(value.DocumentVersionId,160) &&
        TenantAccessClient.Identifier(value.EvidenceId,160) && TenantAccessClient.Identifier(value.RecordedBy,160) && value.RecordedAt != default;
    private static bool Text(string? value,int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value,2)==value;
    private static async Task<(CustomerOwnerSession Session, CustomerOwnerWorkspace Customer)> ResolveAsync(
        HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, bool write)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
        var read = await access.EvaluateCustomerAsync(http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
        var evidence = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
        if (read.Outcome != "Allow" || read.AccessBasis != "Self" || read.ActorId != session.ActorId ||
            read.SubjectVersion != session.SubjectVersion || evidence.Outcome != "Allow" || evidence.AccessBasis != "Self" || evidence.ActorId != read.ActorId ||
            evidence.SubjectVersion != read.SubjectVersion || evidence.CustomerVersion != read.CustomerVersion)
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
            throw new TenantBoundaryException("LIABILITY_RECORD_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductLiabilityRecordClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductLiabilityRecordClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductLiabilityRecordClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<LiabilityRecordList> ListAsync(CustomerOwnerSession session, string customer, CancellationToken ct) =>
        SendAsync<LiabilityRecordList>(session, customer, "", null, ct);
    public Task<ReviewedLiabilityRecord> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct, int? revision = null) =>
        SendAsync<ReviewedLiabilityRecord>(session, customer, "/" + Uri.EscapeDataString(id) + (revision is null ? "" : "?revision=" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<ReviewedLiabilityRecord> CreateAsync(CustomerOwnerSession session, string customer, CreateLiabilityRecord input, CancellationToken ct) =>
        SendAsync<ReviewedLiabilityRecord>(session, customer, "", input, ct);
    public Task<ReviewedLiabilityRecord> ReviseAsync(CustomerOwnerSession session, string customer, string id, ReviseLiabilityRecord input, CancellationToken ct) =>
        SendAsync<ReviewedLiabilityRecord>(session, customer, "/" + Uri.EscapeDataString(id) + "/corrections", input, ct);
    public Task<LiabilityRecordHistory> HistoryAsync(CustomerOwnerSession session, string customer, string id, int? beforeRevision, CancellationToken ct) =>
        SendAsync<LiabilityRecordHistory>(session, customer, "/" + Uri.EscapeDataString(id) + "/history" + (beforeRevision is null ? "" : "?beforeRevision=" + beforeRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<LiabilityRecordSnapshot> SnapshotAsync(CustomerOwnerSession session,string customer,DateOnly asOfDate,string currency,CancellationToken ct) =>
        SendAsync<LiabilityRecordSnapshot>(session,customer,"/snapshot?asOfDate="+asOfDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)+"&currency="+Uri.EscapeDataString(currency),null,ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, object? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/liability-records" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "LIABILITY_RECORD_NOT_FOUND",
                409 => "LIABILITY_OPERATION_CONFLICT", 400 => "LIABILITY_RECORD_INVALID", 413 => "LIABILITY_RECORD_TOO_LARGE", _ => "LIABILITY_RECORD_UNAVAILABLE" };
            if (status == 409)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value) && value.GetString()=="LIABILITY_REVISION_CONFLICT") code="LIABILITY_REVISION_CONFLICT";
            }
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("LIABILITY_RECORD_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
