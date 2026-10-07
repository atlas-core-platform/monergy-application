using Monergy.Contracts;

namespace Monergy.Services.Audit.Application;

public interface ITenantAccessAuditRepository
{
    Task<AccessManagementReceipt> AppendAsync(AccessManagementEvent source, CancellationToken cancellationToken);
    Task<bool> ReadyAsync(CancellationToken cancellationToken);
}

// CID-061's AR-001 producer extension requires a tenant-aware envelope. It is
// deliberately not added to the legacy flat-event allowlist, which loses tenant
// and initiator identity. Evidence remains in the Audit-owned canonical ledger.
public sealed class TenantAccessAudit(ITenantAccessAuditRepository repository)
{
    public Task<AccessManagementReceipt> ConsumeAsync(string trustedTenant, AccessManagementEvent source, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Validate(source, trustedTenant, "audit");
        return repository.AppendAsync(source, cancellationToken);
    }
}
