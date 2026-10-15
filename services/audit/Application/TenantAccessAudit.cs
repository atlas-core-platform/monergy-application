using Monergy.Contracts;

namespace Monergy.Services.Audit.Application;

public interface ITenantAccessAuditRepository
{
    Task<AccessManagementReceipt> AppendAsync(AccessManagementEvent source, CancellationToken cancellationToken);
    Task<bool> ReadyAsync(CancellationToken cancellationToken);
}

public interface ITenantJobAuditRepository
{
    Task<TenantJobAuditReceipt> AppendJobEventAsync(
        string tenantId,
        AuditableEvent source,
        CancellationToken cancellationToken);
}

public sealed record TenantJobAuditReceipt(
    string Recipient,
    string TenantId,
    string SourceEventId,
    string SourceContractId,
    string EventName,
    string EventVersion,
    string Producer,
    string SubjectType,
    string SubjectId,
    string AuditEvidenceId,
    bool Created);

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

// LOCAL/UAT composition for the existing CID-058..060 Job Management events.
// Tenant identity is established by the trusted transport header and is never
// inferred from an untrusted event payload.
public sealed class TenantJobAudit(ITenantJobAuditRepository repository)
{
    public Task<TenantJobAuditReceipt> ConsumeAsync(
        string trustedTenant,
        AuditableEvent source,
        CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Identifier(trustedTenant, 64);
        ArgumentNullException.ThrowIfNull(source);
        var expectedName = source.ContractId switch
        {
            "CID-058" => Vs02ContractNames.JobStarted,
            "CID-059" => Vs02ContractNames.JobCompleted,
            "CID-060" => Vs02ContractNames.JobFailed,
            _ => null,
        };
        if (expectedName is null ||
            source.EventName != expectedName ||
            source.EventVersion != ContractGuard.CurrentVersion ||
            source.Producer != "Job Management Service" ||
            source.SubjectType != "processing-job" ||
            string.IsNullOrWhiteSpace(source.EventId) ||
            string.IsNullOrWhiteSpace(source.SubjectId) ||
            string.IsNullOrWhiteSpace(source.CorrelationId) ||
            source.OccurredAt == default)
        {
            throw new ArgumentException("The Job Management audit event is invalid.", nameof(source));
        }
        return repository.AppendJobEventAsync(trustedTenant, source, cancellationToken);
    }
}
