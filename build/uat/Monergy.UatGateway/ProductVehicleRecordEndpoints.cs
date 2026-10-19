using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public sealed record ProductVehicleRecordList(IReadOnlyList<ReviewedVehicleRecord> Items, bool HasMore, string DataDurability);
public static class ProductVehicleRecordEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters=true, RespectNullableAnnotations=true, UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void AddProductVehicleRecords(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) builder.Services.AddSingleton<ProductVehicleRecordClient>();
    }
    public static void MapProductVehicleRecords(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        var group = app.MapGroup("/product-api/v1/vehicle-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
            { return Results.Json(new { error = "VEHICLE_RECORD_UNAVAILABLE" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "VEHICLE_RECORD_TOO_LARGE" },statusCode:413); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "VEHICLE_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductVehicleRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, false).ConfigureAwait(false);
            var list = await client.ListAsync(session, customer.CustomerId, http.RequestAborted).ConfigureAwait(false);
            if (list.Items is null || list.Items.Count > 200 || list.HasMore && list.Items.Count != 200 || list.Items.Select(x => x?.VehicleRecordId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count || list.Items.Any(item => !Bound(item, customer.CustomerId)))
                throw new TenantBoundaryException("VEHICLE_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, false).ConfigureAwait(false);
            return new ProductVehicleRecordList(list.Items, list.HasMore, "Temporary data");
        });
        group.MapPost("", async (HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductVehicleRecordClient client) =>
        {
            var (session, customer) = await ResolveAsync(http, owners, access, true).ConfigureAwait(false);
            var input = await ReadInputAsync(http).ConfigureAwait(false);
            if (input.Vehicle is null) throw new TenantBoundaryException("VEHICLE_RECORD_INVALID",400);
            var receipt = await client.CreateAsync(session, customer.CustomerId, input, http.RequestAborted).ConfigureAwait(false);
            if (!Bound(receipt, customer.CustomerId) || receipt.OperationId != input.OperationId || receipt.RecordedBy != session.ActorId ||
                receipt.Vehicle != input.Vehicle with { Name = input.Vehicle.Name.Trim(), RegistrationNumber = string.IsNullOrWhiteSpace(input.Vehicle.RegistrationNumber) ? null : input.Vehicle.RegistrationNumber.Trim(), VehicleType = input.Vehicle.VehicleType.Trim() })
                throw new TenantBoundaryException("VEHICLE_RECORD_RESPONSE_INVALID", 503);
            var saved = await client.ReadAsync(session, customer.CustomerId, receipt.VehicleRecordId, http.RequestAborted).ConfigureAwait(false);
            if (saved != receipt) throw new TenantBoundaryException("VEHICLE_RECORD_RESPONSE_INVALID", 503);
            await ConfirmAsync(http, owners, access, session, customer, true).ConfigureAwait(false);
            return saved;
        });
    }
    private static async Task<CreateVehicleRecord> ReadInputAsync(HttpContext http)
    {
        try
        {
            http.Request.EnableBuffering(32768,32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body,cancellationToken:http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            return json.RootElement.Deserialize<CreateVehicleRecord>(InputJson)
                ?? throw new TenantBoundaryException("VEHICLE_RECORD_INVALID",400);
        }
        catch (JsonException) { throw new TenantBoundaryException("VEHICLE_RECORD_INVALID",400); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var vehicle in element.EnumerateObject())
            { if(!names.Add(vehicle.Name)) throw new TenantBoundaryException("VEHICLE_RECORD_INVALID",400); RejectDuplicates(vehicle.Value); }
        }
        else if(element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool Bound(ReviewedVehicleRecord? value, string customer) => value is { Vehicle: not null, Currency: not null, SourceProvenanceId: not null, DocumentVersionId: not null } && value.CustomerId == customer &&
        TenantAccessClient.Identifier(value.VehicleRecordId, 80) && Guid.TryParseExact(value.OperationId, "N", out _) &&
        value.Currency.Length == 3 && value.Currency.All(char.IsAsciiLetterUpper) &&
        Text(value.Vehicle.Name) && OptionalRegistration(value.Vehicle.RegistrationNumber) && Text(value.Vehicle.VehicleType) &&
        Money(value.Vehicle.PurchaseValue) && Money(value.Vehicle.CurrentValue) && value.Vehicle.ValueAsOfDate.Year >= 1900 &&
        value.Vehicle.ValueAsOfDate <= DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime.AddHours(14)) &&
        TenantAccessClient.Identifier(value.Vehicle.SourceFinancialFactId,160) && value.Vehicle.SourceRevision > 0 &&
        TenantAccessClient.Identifier(value.SourceProvenanceId,160) && TenantAccessClient.Identifier(value.DocumentVersionId,160) &&
        TenantAccessClient.Identifier(value.EvidenceId,160) && TenantAccessClient.Identifier(value.RecordedBy,160) && value.RecordedAt != default;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool OptionalRegistration(string? value) => value is null || value.Trim().Length <= 40 && !value.Any(char.IsControl);
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
            throw new TenantBoundaryException("VEHICLE_RECORD_CONTEXT_CHANGED", 409);
    }
}

public sealed class ProductVehicleRecordClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(ContractJson.Options)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    public ProductVehicleRecordClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })) { }
    public ProductVehicleRecordClient(IConfiguration configuration, HttpClient httpClient)
    {
        client = httpClient; client.Timeout = TimeSpan.FromSeconds(10); client.MaxResponseContentBufferSize = 1048576;
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }
    public Task<VehicleRecordList> ListAsync(CustomerOwnerSession session, string customer, CancellationToken ct) =>
        SendAsync<VehicleRecordList>(session, customer, "", null, ct);
    public Task<ReviewedVehicleRecord> ReadAsync(CustomerOwnerSession session, string customer, string id, CancellationToken ct) =>
        SendAsync<ReviewedVehicleRecord>(session, customer, "/" + Uri.EscapeDataString(id), null, ct);
    public Task<ReviewedVehicleRecord> CreateAsync(CustomerOwnerSession session, string customer, CreateVehicleRecord input, CancellationToken ct) =>
        SendAsync<ReviewedVehicleRecord>(session, customer, "", input, ct);
    private async Task<T> SendAsync<T>(CustomerOwnerSession session, string customer, string suffix, CreateVehicleRecord? input, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/vehicle-records" + suffix));
        request.Headers.Add("X-Monergy-Tenant", session.TenantId); request.Headers.Add("X-Monergy-Session", session.AuthenticationContextId);
        if (input is not null) request.Content = JsonContent.Create(input, options: ContractJson.Options);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var code = status switch { 401 => "TENANT_SESSION_INVALID", 403 => "ACCESS_DENIED", 404 => "VEHICLE_SOURCE_NOT_FOUND",
                409 => "VEHICLE_OPERATION_CONFLICT", 400 => "VEHICLE_RECORD_INVALID", 413 => "VEHICLE_RECORD_TOO_LARGE", _ => "VEHICLE_RECORD_UNAVAILABLE" };
            throw new TenantBoundaryException(code, status is 400 or 401 or 403 or 404 or 409 or 413 ? status : 503);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("VEHICLE_RECORD_RESPONSE_INVALID", 503);
    }
    public void Dispose() => client.Dispose();
}
