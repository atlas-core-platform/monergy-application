using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Monergy.Services.DocumentIntelligence;

// Explicit LOCAL/UAT owner review permission supplements current tenant-administrator authority.
// No document bytes, excerpts, identity numbers or financial values enter this workflow.
public sealed class ClassificationReviewPolicy(IConfiguration configuration)
{
    public bool Allows(TenantAdminContext admin) => configuration.GetSection("Monergy:Classification:Reviewers:" + admin.TenantId)
        .GetChildren().Any(x => x.Value == admin.ActorId);
}
public static class ClassificationReviewEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static void MapClassificationReviews(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/administration/classification-reviews");
        group.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            if (http.Request.ContentLength > 4096) return Results.StatusCode(413);
            try { return await next(context); }
            catch (TenantBoundaryException e) { return Results.Json(new { error = e.Code }, statusCode: e.Status); }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or NpgsqlException) { return Results.StatusCode(503); }
        });
        group.MapGet("", async (HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentClassificationRepository repository, ClassificationReviewPolicy policy) => {
            var admin = await access.RequireAdministratorAsync(http); var q = http.Request.Query;
            if (q.Any(x => x.Key is not ("status" or "after") || x.Value.Count != 1) || q.ContainsKey("status") && q["status"].ToString() is not ("unknown" or "all") ||
                q.ContainsKey("after") && (q["after"].ToString().Length != 64 || q["after"].ToString().Any(c => !char.IsAsciiHexDigitLower(c)))) throw new TenantBoundaryException("CLASSIFICATION_QUERY_INVALID", 400);
            var rows = await repository.ListAsync(admin.TenantId, q["status"] != "all", q.ContainsKey("after") ? q["after"].ToString() : null, http.RequestAborted);
            if (rows.Count > 51 || rows.Any(x => !ClassificationRules.Valid(x, admin.TenantId) || bindings.CustomerTenants.TryGetValue(x.CustomerId, out var tenant) && tenant != admin.TenantId) || rows.Select(x => ClassificationRules.Key(x.CustomerId, x.DocumentVersionId)).Distinct().Count() != rows.Count) throw new TenantBoundaryException("CLASSIFICATION_RESULT_INVALID", 503);
            Confirm(admin, await access.RequireAdministratorAsync(http));
            return new ClassificationReviewPage(admin.TenantId, rows.Take(50).ToArray(), rows.Count == 51 ? ClassificationRules.Key(rows[49].CustomerId, rows[49].DocumentVersionId) : null, policy.Allows(admin));
        });
        group.MapGet("/{customerId}/{versionId}", async (string customerId, string versionId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentClassificationRepository repository) => {
            var admin = await access.RequireAdministratorAsync(http); Validate(http, bindings, admin, customerId, versionId);
            var row = await repository.ReadAsync(admin.TenantId, customerId, versionId, http.RequestAborted) ?? throw new TenantBoundaryException("CLASSIFICATION_NOT_FOUND", 404);
            if (!ClassificationRules.Valid(row, admin.TenantId) || row.CustomerId != customerId || row.DocumentVersionId != versionId) throw new TenantBoundaryException("CLASSIFICATION_RESULT_INVALID", 503);
            Confirm(admin, await access.RequireAdministratorAsync(http)); return row;
        });
        group.MapPost("/{customerId}/{versionId}", async (string customerId, string versionId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentClassificationRepository repository, ClassificationReviewPolicy policy, TimeProvider clock) => {
            var admin = await access.RequireAdministratorAsync(http); Validate(http, bindings, admin, customerId, versionId);
            if (!policy.Allows(admin)) throw new TenantBoundaryException("CLASSIFICATION_REVIEW_NOT_GRANTED", 403);
            var change = await ReadChange(http);
            ClassificationRules.ValidateChange(change); Confirm(admin, await access.RequireAdministratorAsync(http));
            return await repository.ReviewAsync(admin.TenantId, customerId, versionId, admin.ActorId, change, clock.GetUtcNow(), http.RequestAborted);
        });
    }
    private static async Task<ClassificationChange> ReadChange(HttpContext http)
    {
        var bytes = new byte[4097]; var length = 0;
        while (length < bytes.Length)
        {
            var count = await http.Request.Body.ReadAsync(bytes.AsMemory(length), http.RequestAborted);
            if (count == 0) break; length += count;
        }
        if (length > 4096) throw new TenantBoundaryException("CLASSIFICATION_REVIEW_TOO_LARGE", 413);
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count()) throw new JsonException();
            return document.RootElement.Deserialize<ClassificationChange>(Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TenantBoundaryException("CLASSIFICATION_REVIEW_INVALID", 400); }
    }
    private static void Confirm(TenantAdminContext before, TenantAdminContext after)
    { if (before != after) throw new TenantBoundaryException("ADMINISTRATOR_CONTEXT_CHANGED", 409); }
    private static void Validate(HttpContext http, TenantBoundaryOptions bindings, TenantAdminContext admin, string customer, string version)
    {
        if (http.Request.Query.Count != 0 || !TenantAccessClient.Identifier(customer, 128) || !TenantAccessClient.Identifier(version, 160)) throw new TenantBoundaryException("CLASSIFICATION_QUERY_INVALID", 400);
        if (bindings.CustomerTenants.TryGetValue(customer, out var tenant) && tenant != admin.TenantId) throw new TenantBoundaryException("CLASSIFICATION_NOT_FOUND", 404);
    }
}
