using System.Text.Json;
using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;

namespace Monergy.Vs02.Tests;

internal static class Vs02TestContext
{
    public const string CustomerId = "customer-001";
    public const string CorrelationId = "correlation-001";
    public const string ContentReference = "reference://evidence/content-001";
    public const string ContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static TrustedSecurityContext Security(string customerId = CustomerId, string consent = "consent-001") =>
        new(
            new ActorContext("actor-001", "CUSTOMER", DateTimeOffset.Parse("2026-09-13T00:00:00Z", CultureInfo.InvariantCulture), "authn-001"),
            new WorkloadContext("test-workload", "workload-identity-001"),
            new AccessContext("financial-evidence-processing", consent, "authorization-001", customerId));

    public static ContractRequest<T> Request<T>(
        string name,
        T payload,
        string requestId,
        string? idempotencyKey = null,
        TrustedSecurityContext? security = null) =>
        new(name, ContractGuard.CurrentVersion, requestId, CorrelationId, "cause-001", security ?? Security(), idempotencyKey, payload);

    public static CreateDocumentVersion CreateVersionPayload() =>
        new(
            "document-001",
            "document-version-001",
            "evidence-001",
            CustomerId,
            "MANUAL_UPLOAD",
            "financial-statement.txt",
            "text/plain",
            ContentReference,
            ContentSha256,
            DateTimeOffset.Parse("2026-09-13T00:00:00Z", CultureInfo.InvariantCulture));

    public static string Fixture =>
        "INCOME|Salary|125000.00|INR|2026-08-31|page:1|0.98\n" +
        "EXPENSE|Rent|25000.00|INR|2026-08-31|page:1|0.97";
}

internal sealed class CapturingTelemetry : ILifecycleTelemetry
{
    public List<LifecycleSignal> Signals { get; } = [];

    public void Record(LifecycleSignal signal) => Signals.Add(signal);
}

internal sealed class SerializedEvidenceReader(
    EvidenceApplication evidence,
    ReferenceEvidenceContentStore contentStore) : IEvidenceContentReader
{
    public async Task<EvidenceContent?> ReadAsync(
        string documentVersionId,
        string customerId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var request = Vs02TestContext.Request(
            Vs02ContractNames.GetEvidenceReference,
            new GetEvidenceReference(documentVersionId, customerId),
            $"request-reference-{documentVersionId}",
            security: security) with
        { CorrelationId = correlationId };
        var wireRequest = JsonSerializer.Deserialize<ContractRequest<GetEvidenceReference>>(
            JsonSerializer.Serialize(request, ContractJson.Options),
            ContractJson.Options)!;
        var result = await evidence.GetEvidenceReferenceAsync(wireRequest, cancellationToken);
        var wireResult = JsonSerializer.Deserialize<ContractResult<EvidenceReference>>(
            JsonSerializer.Serialize(result, ContractJson.Options),
            ContractJson.Options)!;
        if (wireResult.Outcome != ContractOutcome.Success || wireResult.Data is null)
        {
            return null;
        }

        var content = await contentStore.ReadAsync(wireResult.Data.ContentReference, cancellationToken);
        return content is null ? null : new EvidenceContent(wireResult.Data, content);
    }
}

internal static class AuditEventMapper
{
    public static AuditableEvent From<TPayload>(DomainEvent<TPayload> source) =>
        new(
            source.ContractId,
            source.EventId,
            source.EventName,
            source.EventVersion,
            source.OccurredAt,
            source.CorrelationId,
            source.CausationId,
            source.Producer,
            source.SubjectType,
            source.SubjectId);
}
