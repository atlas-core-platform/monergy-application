namespace Monergy.Platform;

// Minimized boundary evidence. Never accepts arbitrary bodies, URLs, headers or claims of domain completion.
public sealed record ProductAuditRecord(string TenantId,string EventId,string RequestId,string Phase,
    string? ActorId,string ActorType,string? CustomerId,string Action,string Source,string ResourceType,
    string? ResourceId,string? DocumentId,DateTimeOffset OccurredAt,string Outcome,string Reason,
    string? IpAddress=null,string? Device=null);
public static class ProductAuditProtocol
{
    public static readonly string[] Actions=["LOGIN","CONSENT_GRANT","CONSENT_REVOKE","DOCUMENT_UPLOAD","DOCUMENT_VIEW","DOCUMENT_UPDATE","REPORT_GENERATION","DATA_EXPORT","API_ACCESS"];
    public static void Validate(ProductAuditRecord r)
    {
        if(!TenantAccessClient.Identifier(r.TenantId,64)||!Guid.TryParseExact(r.RequestId,"N",out _)||r.RequestId.Any(c=>c is >='A' and <='F')||
            r.Phase is not ("REQUESTED" or "RESULT")||r.EventId!=r.RequestId+":"+r.Phase||
            r.ActorType is not ("USER" or "UNAUTHENTICATED" or "UNRECORDED")||r.ActorType=="USER"&&!TenantAccessClient.Identifier(r.ActorId,128)||r.ActorType!="USER"&&r.ActorId is not null||
            r.CustomerId is not null&&!TenantAccessClient.Identifier(r.CustomerId,128)||!Actions.Contains(r.Action,StringComparer.Ordinal)||r.Source!="Product Gateway"||
            !TenantAccessClient.Identifier(r.ResourceType,64)||r.ResourceId is not null&&!TenantAccessClient.Identifier(r.ResourceId,200)||r.DocumentId is not null&&!TenantAccessClient.Identifier(r.DocumentId,160)||
            r.OccurredAt<DateTimeOffset.UnixEpoch||r.Outcome is not ("REQUESTED" or "HTTP_SUCCEEDED" or "HTTP_FAILED" or "ACCEPTED" or "UNKNOWN")||
            (r.Phase=="REQUESTED")!=(r.Outcome=="REQUESTED")||!TenantAccessClient.Identifier(r.Reason,100)||r.IpAddress is not null||r.Device is not null)
            throw new TenantBoundaryException("AUDIT_RECORD_INVALID",400);
    }
}
