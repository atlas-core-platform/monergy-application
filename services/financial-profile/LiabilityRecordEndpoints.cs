using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile;

public static class LiabilityRecordEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void MapLiabilityRecords(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/local/v1/customers/{customerId}/liability-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (JsonException) { return Results.Json(new { error = "LIABILITY_RECORD_INVALID" }, statusCode: 400); }
            catch (IOException) { return Results.Json(new { error = "LIABILITY_RECORD_TOO_LARGE" }, statusCode: 413); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "LIABILITY_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (string customerId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LiabilityRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.ListAsync(tenant, customerId, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapGet("/snapshot",async (string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,LiabilityRecordsApplication owner)=>
        {
            var(tenant,_)=await AuthorizeAsync(http,customerId,access,bindings,false).ConfigureAwait(false);
            var(date,currency)=SnapshotSelection(http.Request);
            return await owner.SnapshotAsync(tenant,customerId,date,currency,http.RequestAborted).ConfigureAwait(false);
        });
        group.MapGet("/{liabilityRecordId}", async (string customerId, string liabilityRecordId, int? revision, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LiabilityRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.ReadAsync(tenant, customerId, liabilityRecordId, http.RequestAborted, revision).ConfigureAwait(false);
        });
        group.MapGet("/{liabilityRecordId}/history", async (string customerId, string liabilityRecordId, int? beforeRevision, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LiabilityRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.HistoryAsync(tenant, customerId, liabilityRecordId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapPost("/{liabilityRecordId}/corrections", async (string customerId, string liabilityRecordId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LiabilityRecordsApplication owner) =>
        {
            var (tenant, actor) = await AuthorizeAsync(http, customerId, access, bindings, true).ConfigureAwait(false);
            http.Request.EnableBuffering(32768, 32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            var input = json.RootElement.Deserialize<ReviseLiabilityRecord>(Json) ?? throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
            return await owner.ReviseAsync(tenant, customerId, actor, liabilityRecordId, input, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapPost("", async (string customerId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LiabilityRecordsApplication owner) =>
        {
            var (tenant, actor) = await AuthorizeAsync(http, customerId, access, bindings, true).ConfigureAwait(false);
            http.Request.EnableBuffering(32768, 32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            var input = json.RootElement.Deserialize<CreateLiabilityRecord>(Json) ?? throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
            return await owner.CreateAsync(tenant, customerId, actor, input, http.RequestAborted).ConfigureAwait(false);
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
    private static async Task<(string Tenant, string Actor)> AuthorizeAsync(HttpContext http, string customer, TenantAccessClient access, TenantBoundaryOptions bindings, bool write)
    {
        var (tenant, _) = TenantAccessClient.Headers(http);
        if (!bindings.AllowsCustomerLookup(customer, tenant))
            throw new TenantBoundaryException("CUSTOMER_TENANT_MISMATCH", 403);
        var read = await access.EvaluateCustomerAsync(http, "financial-profile.profile.read", customer).ConfigureAwait(false);
        var evidence = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer).ConfigureAwait(false);
        if (read.Outcome != "Allow" || read.AccessBasis != "Self" || evidence.Outcome != "Allow" || evidence.AccessBasis != "Self" || evidence.ActorId != read.ActorId || evidence.SubjectVersion != read.SubjectVersion || evidence.CustomerVersion != read.CustomerVersion)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        if (write)
        {
            var update = await access.EvaluateCustomerAsync(http, "financial-profile.profile.update", customer).ConfigureAwait(false);
            if (update.Outcome != "Allow" || update.AccessBasis != "Self" || update.ActorId != read.ActorId ||
                update.SubjectVersion != read.SubjectVersion || update.CustomerVersion != read.CustomerVersion)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
        }
        return (tenant, read.ActorId);
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
