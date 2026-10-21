using System.Security.Cryptography;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;

namespace Monergy.Services.Evidence;

public static class KycEvidenceEndpoints
{
    public static void MapKycEvidence(this WebApplication app)
    {
        app.MapGet("/local/v1/customers/{customerId}/kyc-evidence/{versionId}", async (string customerId, string versionId, HttpContext http,
            TenantAccessClient access, TenantBoundaryOptions bindings, IEvidenceRepository repo) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (http.Request.Query.Count != 0 || !TenantAccessClient.Identifier(customerId,128) || !TenantAccessClient.Identifier(versionId,160)) throw new TenantBoundaryException("KYC_EVIDENCE_INVALID",400);
                if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") throw new TenantBoundaryException("ACCESS_DENIED",403);
                var (tenant, _) = TenantAccessClient.Headers(http);
                if (!bindings.AllowsCustomerLookup(customerId,tenant)) throw new TenantBoundaryException("ACCESS_DENIED",403);
                var before = await access.EvaluateCustomerAsync(http,"evidence.document.read",customerId);
                if (before.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED",403);
                var row = await repo.GetVersionAsync(versionId,http.RequestAborted);
                if (row is null || row.CustomerId != customerId || row.TenantId != tenant) throw new TenantBoundaryException("KYC_EVIDENCE_NOT_FOUND",404);
                if (row.ContentType is not ("application/pdf" or "image/png" or "image/jpeg") || row.MalwareCheck is null) throw new TenantBoundaryException("KYC_EVIDENCE_UNSUPPORTED",422);
                var content = http.RequestServices.GetService<ReferenceEvidenceContentStore>();
                var bytes = content is null ? null : await content.ReadBytesAsync(row.ContentReference,http.RequestAborted);
                if (bytes is null || bytes.Length is <1 or >1_048_576 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != row.ContentSha256) throw new TenantBoundaryException("KYC_EVIDENCE_UNAVAILABLE",503);
                var after = await access.EvaluateCustomerAsync(http,"evidence.document.read",customerId);
                if (after.Outcome != "Allow" || before.ActorId != after.ActorId || before.SubjectVersion != after.SubjectVersion || before.PolicyVersion != after.PolicyVersion || before.CustomerVersion != after.CustomerVersion || before.ConsentVersion != after.ConsentVersion) throw new TenantBoundaryException("ACCESS_CHANGED",403);
                return Results.Ok(new { tenantId=tenant,customerId,documentVersionId=versionId,fileName=row.OriginalFileName,sha256=row.ContentSha256,contentType=row.ContentType,contentBase64=Convert.ToBase64String(bytes) });
            }
            catch (TenantBoundaryException e) { return Results.Json(new {error=e.Code},statusCode:e.Status); }
        });
    }
}
