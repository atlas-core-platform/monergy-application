using Microsoft.AspNetCore.Http;
namespace Monergy.Platform;
public static class ReviewedRecordPaging
{
    public static string? ReadAfter(HttpRequest request)
    {
        if (request.Query.Count == 0) return null;
        if (request.Query.Count != 1 || request.Query["afterId"].Count != 1) throw new TenantBoundaryException("RECORD_PAGE_INVALID", 400);
        return Validate(request.Query["afterId"].ToString());
    }
    public static string? Validate(string? afterId)
    {
        if (afterId is not null && !TenantAccessClient.Identifier(afterId, 80)) throw new TenantBoundaryException("RECORD_PAGE_INVALID", 400);
        return afterId;
    }
}
