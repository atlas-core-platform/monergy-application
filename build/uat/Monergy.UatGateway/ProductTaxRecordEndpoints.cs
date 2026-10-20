using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductTaxRecordList(IReadOnlyList<ReviewedTaxRecord> Items, bool HasMore, string DataDurability);
public static class ProductTaxRecordEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductTaxRecords(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductTaxRecordClient>();
    }
    public static void MapProductTaxRecords(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/tax-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "TAX_RECORD_RECORD_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "TAX_RECORD_RECORD_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "TAX_RECORD_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductTaxRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items is null || list.Items.Count > 200 || list.HasMore && list.Items.Count != 200 || list.Items.Select(x => x?.TaxRecordId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count || list.Items.Any(item => !Bound(item, customer.CustomerId)))
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductTaxRecordList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductTaxRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync<CreateTaxRecord>(http).ConfigureAwait(false);
            if (input.Tax is null) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId ||
                receipt.Tax != input.Tax with { Name = input.Tax.Name.Trim(), Jurisdiction = input.Tax.Jurisdiction.Trim(), TaxType = input.Tax.TaxType.Trim(), TaxYearLabel = input.Tax.TaxYearLabel.Trim() })
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.TaxRecordId, http.RequestAborted,1).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
        group.MapGet("/{taxRecordId}/history", async (string taxRecordId, int? beforeRevision, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductTaxRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(taxRecordId,80) || beforeRevision is < 1) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, taxRecordId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.TaxRecordId != taxRecordId || original.Revision != 1)
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID",503);
            var history = await client.HistoryAsync(session, customer.CustomerId, taxRecordId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
            if (history.Items is null || history.Items.Count > 50 || history.Items.Any(x => !Bound(x,customer.CustomerId) || x.TaxRecordId != taxRecordId || beforeRevision is not null && x.Revision >= beforeRevision ||
                x.Currency != original.Currency || x.SourceProvenanceId != original.SourceProvenanceId || x.DocumentVersionId != original.DocumentVersionId || x.EvidenceId != original.EvidenceId ||
                x.Tax.SourceFinancialFactId != original.Tax.SourceFinancialFactId || x.Tax.SourceRevision != original.Tax.SourceRevision) ||
                history.Items.Zip(history.Items.Skip(1)).Any(pair => pair.First.Revision != pair.Second.Revision + 1) ||
                history.HasMore && (history.Items.Count != 50 || history.NextBeforeRevision != history.Items[^1].Revision) || !history.HasMore && (history.NextBeforeRevision is not null || history.Items.Count > 0 && history.Items[^1].Revision != 1) ||
                history.Items.Count == 0 && beforeRevision != 1)
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID",503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return history;
        });
        group.MapPost("/{taxRecordId}/corrections", async (string taxRecordId, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductTaxRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(taxRecordId,80)) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400);
            var input = await ReadInputAsync<ReviseTaxRecord>(http).ConfigureAwait(false);
            if (input.Tax is null || input.ExpectedRevision is < 1 or int.MaxValue) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400);
            var original = await client.ReadAsync(session, customer.CustomerId, taxRecordId, http.RequestAborted, 1).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || original.TaxRecordId != taxRecordId)
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID",503);
            var receipt = await client.ReviseAsync(session, customer.CustomerId, taxRecordId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(original,customer.CustomerId) || original.Revision != 1 || !Bound(receipt,customer.CustomerId) || original.TaxRecordId != taxRecordId || receipt.TaxRecordId != taxRecordId ||
                receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId || receipt.Revision != input.ExpectedRevision + 1 ||
                receipt.Tax != input.Tax with { Name = input.Tax.Name.Trim(), Jurisdiction = input.Tax.Jurisdiction.Trim(), TaxType = input.Tax.TaxType.Trim(), TaxYearLabel = input.Tax.TaxYearLabel.Trim() } ||
                receipt.VerificationStatus != original.VerificationStatus || receipt.Currency != original.Currency || receipt.SourceProvenanceId != original.SourceProvenanceId || receipt.DocumentVersionId != original.DocumentVersionId || receipt.EvidenceId != original.EvidenceId ||
                receipt.Tax.SourceFinancialFactId != original.Tax.SourceFinancialFactId || receipt.Tax.SourceRevision != original.Tax.SourceRevision)
                throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID",503);
            var saved = await client.ReadAsync(session, customer.CustomerId, taxRecordId, http.RequestAborted, receipt.Revision).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID",503);
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
                ?? throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            { if(!names.Add(property.Name)) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID",400); RejectDuplicates(property.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool Bound(ReviewedTaxRecord? value, string customer) => value is { Tax: not null, Currency: not null, SourceProvenanceId: not null, DocumentVersionId: not null } && value.CustomerId == customer &&
        value.Revision > 0 && TenantAccessClient.Identifier(value.TaxRecordId, 80) && Guid.TryParseExact(value.OperationId, "N", out _) &&
        value.Currency.Length == 3 && value.Currency.All(char.IsAsciiLetterUpper) &&
        Text(value.Tax.Name) && Text(value.Tax.Jurisdiction) && Text(value.Tax.TaxType) &&
        Money(value.Tax.Amount) && Text(value.Tax.TaxYearLabel,30) && value.VerificationStatus == "CUSTOMER_REVIEWED" &&
        value.Tax.AmountKind is "PAID" or "DUE" or "WITHHELD" &&
        value.Tax.PeriodStart.Year >= 1900 && value.Tax.PeriodEnd >= value.Tax.PeriodStart &&
        value.Tax.PeriodEnd <= DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)) &&
        TenantAccessClient.Identifier(value.Tax.SourceFinancialFactId,160) && value.Tax.SourceRevision > 0 &&
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
            throw new TenantBoundaryException("TAX_RECORD_RECORD_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductTaxRecordClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductTaxRecordClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductTaxRecordClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<TaxRecordList> ListAsync(CustomerOwnerSession session, string customer, CancellationToken ct) =>
        SendAsync<TaxRecordList>(session, customer, "", null, ct);
    public Task<ReviewedTaxRecord> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct, int? revision = null) =>
        SendAsync<ReviewedTaxRecord>(session, customer, "/" + Uri.EscapeDataString(id) + (revision is null ? "" : "?revision=" + revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    public Task<ReviewedTaxRecord> CreateAsync(CustomerOwnerSession session, string customer, CreateTaxRecord input, CancellationToken ct) =>
        SendAsync<ReviewedTaxRecord>(session, customer, "", input, ct);
    public Task<ReviewedTaxRecord> ReviseAsync(CustomerOwnerSession session, string customer, string id, ReviseTaxRecord input, CancellationToken ct) =>
        SendAsync<ReviewedTaxRecord>(session, customer, "/" + Uri.EscapeDataString(id) + "/corrections", input, ct);
    public Task<TaxRecordHistory> HistoryAsync(CustomerOwnerSession session, string customer, string id, int? beforeRevision, CancellationToken ct) =>
        SendAsync<TaxRecordHistory>(session, customer, "/" + Uri.EscapeDataString(id) + "/history" + (beforeRevision is null ? "" : "?beforeRevision=" + beforeRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, object? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/tax-records" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "TAX_RECORD_RECORD_NOT_FOUND",
                409 => "TAX_RECORD_OPERATION_CONFLICT", 400 => "TAX_RECORD_RECORD_INVALID", 413 => "TAX_RECORD_RECORD_TOO_LARGE", _ => "TAX_RECORD_RECORD_UNAVAILABLE" };
            if (status == 409)
            {
                var error=await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                if(error.TryGetProperty("error",out var value) && value.GetString()=="TAX_RECORD_REVISION_CONFLICT") code="TAX_RECORD_REVISION_CONFLICT";
            }
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("TAX_RECORD_RECORD_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
