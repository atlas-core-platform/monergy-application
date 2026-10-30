using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductInsuranceRecordList(IReadOnlyList<ReviewedInsuranceRecord> Items, bool HasMore, string DataDurability);
public static class ProductInsuranceRecordEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductInsuranceRecords(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductInsuranceRecordClient>();
    }
    public static void MapProductInsuranceRecords(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/insurance-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "INSURANCE_POLICY_RECORD_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "INSURANCE_POLICY_RECORD_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "INSURANCE_POLICY_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductInsuranceRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items is null || list.Items.Count > 200 || list.HasMore && list.Items.Count != 200 || list.Items.Select(x => x?.InsuranceRecordId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count || list.Items.Any(item => !Bound(item, customer.CustomerId)))
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductInsuranceRecordList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductInsuranceRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync<CreateInsuranceRecord>(http).ConfigureAwait(false);
            if (input.Insurance is null) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId ||
                receipt.Insurance != input.Insurance with { Name = input.Insurance.Name.Trim(), Provider = input.Insurance.Provider.Trim(), PolicyType = input.Insurance.PolicyType.Trim() })
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.InsuranceRecordId, http.RequestAborted,1).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapGet("/{insuranceRecordId}/history", async (string insuranceRecordId, int? beforeRevision, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductInsuranceRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(insuranceRecordId,80) || beforeRevision is < 1) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.InsuranceRecordId != insuranceRecordId || original.Revision != 1)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID",503);
            var history = await client.HistoryAsync(session, customer.CustomerId, insuranceRecordId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
            if (history.Items is null || history.Items.Count > 50 || history.Items.Any(x => !Bound(x,customer.CustomerId) || x.InsuranceRecordId != insuranceRecordId || beforeRevision is not null && x.Revision >= beforeRevision ||
                x.Currency != original.Currency || x.SourceProvenanceId != original.SourceProvenanceId || x.DocumentVersionId != original.DocumentVersionId || x.EvidenceId != original.EvidenceId ||
                x.Insurance.SourceFinancialFactId != original.Insurance.SourceFinancialFactId || x.Insurance.SourceRevision != original.Insurance.SourceRevision) ||
                history.Items.Zip(history.Items.Skip(1)).Any(pair => pair.First.Revision != pair.Second.Revision + 1) ||
                history.HasMore && (history.Items.Count != 50 || history.NextBeforeRevision != history.Items[^1].Revision) || !history.HasMore && (history.NextBeforeRevision is not null || history.Items.Count > 0 && history.Items[^1].Revision != 1) ||
                history.Items.Count == 0 && beforeRevision != 1)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return history;
        });
        group.MapPost("/{insuranceRecordId}/corrections", async (string insuranceRecordId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductInsuranceRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(insuranceRecordId,80)) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400);
            var input = await ReadInputAsync<ReviseInsuranceRecord>(http).ConfigureAwait(false);
            if (input.Insurance is null || input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || original.InsuranceRecordId != insuranceRecordId)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID",503);
            var previous = input.ExpectedRevision == 1 ? original :
                await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, input.ExpectedRevision).ConfigureAwait(false);
            if (!Bound(previous, customer.CustomerId) || previous.Revision != input.ExpectedRevision || previous.InsuranceRecordId != insuranceRecordId)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            var receipt = await client.ReviseAsync(session, customer.CustomerId, insuranceRecordId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || !Bound(receipt,customer.CustomerId) || original.InsuranceRecordId != insuranceRecordId || receipt.InsuranceRecordId != insuranceRecordId ||
                receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 ||
                receipt.Insurance != input.Insurance with { Name = input.Insurance.Name.Trim(), Provider = input.Insurance.Provider.Trim(), PolicyType = input.Insurance.PolicyType.Trim() } ||
                receipt.Currency != original.Currency || receipt.SourceProvenanceId != original.SourceProvenanceId || receipt.DocumentVersionId != original.DocumentVersionId || receipt.EvidenceId != original.EvidenceId ||
                receipt.Insurance.SourceFinancialFactId != original.Insurance.SourceFinancialFactId || receipt.Insurance.SourceRevision != original.Insurance.SourceRevision ||
                receipt.LifecycleStatus != previous.LifecycleStatus || receipt.LifecycleAsOfDate != previous.LifecycleAsOfDate)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID",503);
            var saved = await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapPost("/{insuranceRecordId}/lifecycle", async (string insuranceRecordId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductInsuranceRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(insuranceRecordId, 80)) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
            var input = await ReadInputAsync<ReviewInsuranceRecordStatus>(http).ConfigureAwait(false);
            if (input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
            var previous = await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, input.ExpectedRevision).ConfigureAwait(false);
            if (!Bound(previous, customer.CustomerId) || previous.InsuranceRecordId != insuranceRecordId)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            var receipt = await client.ReviewStatusAsync(session, customer.CustomerId, insuranceRecordId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.InsuranceRecordId != insuranceRecordId || receipt.OperationId != input.OperationId ||
                receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 || receipt.LifecycleStatus != input.Status ||
                receipt.LifecycleAsOfDate != input.AsOfDate || receipt.Insurance != previous.Insurance || receipt.Currency != previous.Currency ||
                receipt.SourceProvenanceId != previous.SourceProvenanceId || receipt.DocumentVersionId != previous.DocumentVersionId || receipt.EvidenceId != previous.EvidenceId)
                throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, insuranceRecordId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
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
                ?? throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var insurance in element.EnumerateObject())
            { if(!names.Add(insurance.Name)) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID",400); RejectDuplicates(insurance.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    internal static bool Bound(ReviewedInsuranceRecord? value, string customer) => value is { Insurance: not null, Currency: not null, SourceProvenanceId: not null, DocumentVersionId: not null } && value.CustomerId == customer &&
        value.Revision > 0 && TenantAccessClient.Identifier(value.InsuranceRecordId, 80) && Guid.TryParseExact(value.OperationId, "N", out _) &&
        value.Currency.Length == 3 && value.Currency.All(char.IsAsciiLetterUpper) &&
        Text(value.Insurance.Name) && Text(value.Insurance.Provider) && Text(value.Insurance.PolicyType) &&
        value.Insurance.MaturityDate is not { Year: < 1900 } && Money(value.Insurance.SumAssured) && Money(value.Insurance.PremiumAmount) && value.Insurance.PolicyAsOfDate.Year >= 1900 &&
        value.Insurance.PolicyAsOfDate <= DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)) &&
        TenantAccessClient.Identifier(value.Insurance.SourceFinancialFactId,160) && value.Insurance.SourceRevision > 0 &&
        TenantAccessClient.Identifier(value.SourceProvenanceId,160) && TenantAccessClient.Identifier(value.DocumentVersionId,160) &&
        TenantAccessClient.Identifier(value.EvidenceId,160) && TenantAccessClient.Identifier(value.RecordedBy,160) && value.RecordedAt != default &&
        (value.LifecycleStatus == "UNREVIEWED" && value.LifecycleAsOfDate is null ||
         value.LifecycleStatus is "ACTIVE" or "CLOSED" && value.LifecycleAsOfDate is { } statusDate && statusDate >= value.Insurance.PolicyAsOfDate);
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value,2)==value;
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
            throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductInsuranceRecordClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductInsuranceRecordClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductInsuranceRecordClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<InsuranceRecordList> ListAsync(CustomerOwnerSession session, string customer, CancellationToken ct) =>
        SendAsync<InsuranceRecordList>(session, customer, "", null, ct);
    public Task<ReviewedInsuranceRecord> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct, int? revision = null) =>
        SendAsync<ReviewedInsuranceRecord>(session, customer, "/" + Uri.EscapeDataString(id) + (revision is null ? "" : "?revision=" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<ReviewedInsuranceRecord> CreateAsync(CustomerOwnerSession session, string customer, CreateInsuranceRecord input, CancellationToken ct) =>
        SendAsync<ReviewedInsuranceRecord>(session, customer, "", input, ct);
    public Task<ReviewedInsuranceRecord> ReviseAsync(CustomerOwnerSession session, string customer, string id, ReviseInsuranceRecord input, CancellationToken ct) =>
        SendAsync<ReviewedInsuranceRecord>(session, customer, "/" + Uri.EscapeDataString(id) + "/corrections", input, ct);
    public Task<ReviewedInsuranceRecord> ReviewStatusAsync(CustomerOwnerSession session, string customer, string id, ReviewInsuranceRecordStatus input, CancellationToken ct) =>
        SendAsync<ReviewedInsuranceRecord>(session, customer, "/" + Uri.EscapeDataString(id) + "/lifecycle", input, ct);
    public Task<InsuranceRecordHistory> HistoryAsync(CustomerOwnerSession session, string customer, string id, int? beforeRevision, CancellationToken ct) =>
        SendAsync<InsuranceRecordHistory>(session, customer, "/" + Uri.EscapeDataString(id) + "/history" + (beforeRevision is null ? "" : "?beforeRevision=" + beforeRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, object? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/insurance-records" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "INSURANCE_POLICY_RECORD_NOT_FOUND",
                409 => "INSURANCE_POLICY_OPERATION_CONFLICT", 400 => "INSURANCE_POLICY_RECORD_INVALID", 413 => "INSURANCE_POLICY_RECORD_TOO_LARGE", _ => "INSURANCE_POLICY_RECORD_UNAVAILABLE" };
            if (status == 409)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value) && value.GetString()=="INSURANCE_POLICY_REVISION_CONFLICT") code="INSURANCE_POLICY_REVISION_CONFLICT";
            }
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
