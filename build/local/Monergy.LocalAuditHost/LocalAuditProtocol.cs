using Monergy.Contracts;
using Monergy.Services.Audit.Application;

namespace Monergy.LocalAuditHost;

public static class LocalAuditProtocol
{
    public const string Recipient = "Audit Service LOCAL composition";

    public static bool IsValidReportingEvent(AuditableEvent source) =>
        source.ContractId == "CID-053" &&
        source.EventName == ReportingContractNames.ReportGenerated &&
        source.EventVersion == ContractGuard.CurrentVersion &&
        source.Producer == "Reporting Service" &&
        source.SubjectType == "Report" &&
        !string.IsNullOrWhiteSpace(source.EventId) &&
        !string.IsNullOrWhiteSpace(source.SubjectId) &&
        source.OccurredAt != default &&
        !string.IsNullOrWhiteSpace(source.CorrelationId);

    public static LocalAuditReceipt Receipt(AuditableEvent source, AuditIngestionResult result) => new(
        Recipient,
        source.EventId,
        source.ContractId,
        source.EventName,
        source.EventVersion,
        source.Producer,
        source.SubjectType,
        source.SubjectId,
        result.Record.AuditEvidenceId,
        result.Created);
}

public sealed record LocalAuditReceipt(
    string Recipient,
    string SourceEventId,
    string SourceContractId,
    string EventName,
    string EventVersion,
    string Producer,
    string SubjectType,
    string SubjectId,
    string AuditEvidenceId,
    bool Created);
