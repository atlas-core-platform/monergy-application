using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile;

public static class LoanRecordEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static void MapLoanRecords(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/local/v1/customers/{customerId}/loan-records");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException error) { return Results.Json(new { error = error.Code }, statusCode: error.Status); }
            catch (JsonException) { return Results.Json(new { error = "LOAN_RECORD_INVALID" }, statusCode: 400); }
            catch (IOException) { return Results.Json(new { error = "LOAN_RECORD_TOO_LARGE" }, statusCode: 413); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "LOAN_RECORD_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", async (string customerId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LoanRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.ListAsync(tenant, customerId, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapGet("/{loanRecordId}", async (string customerId, string loanRecordId, int? revision, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LoanRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.ReadAsync(tenant, customerId, loanRecordId, http.RequestAborted, revision).ConfigureAwait(false);
        });
        group.MapGet("/{loanRecordId}/history", async (string customerId, string loanRecordId, int? beforeRevision, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LoanRecordsApplication owner) =>
        {
            var (tenant, _) = await AuthorizeAsync(http, customerId, access, bindings, false).ConfigureAwait(false);
            return await owner.HistoryAsync(tenant, customerId, loanRecordId, beforeRevision, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapPost("/{loanRecordId}/corrections", async (string customerId, string loanRecordId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LoanRecordsApplication owner) =>
        {
            var (tenant, actor) = await AuthorizeAsync(http, customerId, access, bindings, true).ConfigureAwait(false);
            http.Request.EnableBuffering(32768, 32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            var input = json.RootElement.Deserialize<ReviseLoanRecord>(Json) ?? throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
            return await owner.ReviseAsync(tenant, customerId, actor, loanRecordId, input, http.RequestAborted).ConfigureAwait(false);
        });
        group.MapPost("", async (string customerId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, LoanRecordsApplication owner) =>
        {
            var (tenant, actor) = await AuthorizeAsync(http, customerId, access, bindings, true).ConfigureAwait(false);
            http.Request.EnableBuffering(32768, 32768);
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
            RejectDuplicates(json.RootElement);
            var input = json.RootElement.Deserialize<CreateLoanRecord>(Json) ?? throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
            return await owner.CreateAsync(tenant, customerId, actor, input, http.RequestAborted).ConfigureAwait(false);
        });
    }
    private static async Task<(string Tenant, string Actor)> AuthorizeAsync(HttpContext http, string customer, TenantAccessClient access, TenantBoundaryOptions bindings, bool write)
    {
        var (tenant, _) = TenantAccessClient.Headers(http);
        if (!bindings.CustomerTenants.TryGetValue(customer, out var sourceTenant) || sourceTenant != tenant)
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
