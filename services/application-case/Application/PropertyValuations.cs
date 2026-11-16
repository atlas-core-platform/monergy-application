using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Monergy.Services.ApplicationCase.Application;

public sealed record PropertyValuerContext(
    string TenantId,
    string ActorId,
    long PolicyVersion,
    long SubjectVersion);

public sealed record PropertyValuationContext(
    string TenantId,
    string ActorId,
    string CustomerId,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion);

public sealed record PropertyEvidenceView(
    string Code,
    string Label,
    string Owner,
    string Reference,
    long AssignedVersion,
    long CurrentVersion,
    bool Inspected,
    DateTimeOffset? InspectedAt);

public sealed record PropertyValuationEvidenceSnapshot(
    string Code,
    string Owner,
    string Reference,
    long Version);

public sealed record PropertyValuationRevisionView(
    int Revision,
    decimal MarketValue,
    string Currency,
    DateOnly EffectiveDate,
    string Basis,
    string Findings,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    long PolicyVersion,
    long SubjectVersion,
    long CustomerVersion,
    long ConsentVersion,
    IReadOnlyList<PropertyValuationEvidenceSnapshot> Evidence);

public sealed record PropertyValuationView(
    string ValuationId,
    string PropertyReference,
    string PropertyLabel,
    string State,
    int Version,
    DateTimeOffset AssignedAt,
    IReadOnlyList<PropertyEvidenceView> Evidence,
    IReadOnlyList<PropertyValuationRevisionView> Revisions,
    string DataDurability);

public sealed record CustomerPropertyValuationView(
    string ValuationId,
    string PropertyReference,
    string PropertyLabel,
    int Revision,
    decimal MarketValue,
    string Currency,
    DateOnly EffectiveDate,
    string Basis,
    string Findings,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    int EvidenceCount,
    string DataDurability);

public sealed record InspectPropertyEvidence(string OperationId, int ExpectedVersion);
public sealed record RecordPropertyValuation(
    string OperationId,
    int ExpectedVersion,
    decimal MarketValue,
    string Currency,
    DateOnly EffectiveDate,
    string Basis,
    string Findings);

public interface IPropertyValuationApplication
{
    Task<IReadOnlyList<PropertyValuationView>> ListAsync(
        PropertyValuerContext context,
        Func<string, CancellationToken, Task<PropertyValuationContext>> authorize,
        CancellationToken ct);
    Task<IReadOnlyList<CustomerPropertyValuationView>> ListCustomerResultsAsync(
        CaseActorContext customer,
        CancellationToken ct);
    Task<PropertyValuationView> InspectEvidenceAsync(
        string valuationId,
        string evidenceCode,
        PropertyValuerContext before,
        InspectPropertyEvidence request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm,
        CancellationToken ct);
    Task<PropertyValuationView> RecordValuationAsync(
        string valuationId,
        PropertyValuerContext before,
        RecordPropertyValuation request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm,
        CancellationToken ct);
}

public static class PropertyValuationPolicy
{
    public const string Capability = "application-case.property-valuation.manage";

    public static string Identifier(string value, string code, int maximum = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new ApplicationCaseException(code, 400);
        return value;
    }

    public static string Operation(string value)
    {
        if (value.Length != 32 || value.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new ApplicationCaseException("OPERATION_ID_INVALID", 400);
        return value;
    }

    public static string Text(string value, string code, int minimum, int maximum)
    {
        var result = value.Trim();
        if (result.Length < minimum || result.Length > maximum || result.Any(char.IsControl))
            throw new ApplicationCaseException(code, 400);
        return result;
    }

    public static string Currency(string value)
    {
        if (value.Length != 3 || value.Any(character => character is < 'A' or > 'Z'))
            throw new ApplicationCaseException("PROPERTY_VALUATION_CURRENCY_INVALID", 400);
        return value;
    }

    public static void Request(int expectedVersion, decimal marketValue, DateOnly effectiveDate, DateOnly today)
    {
        if (expectedVersion < 1)
            throw new ApplicationCaseException("PROPERTY_VALUATION_VERSION_INVALID", 400);
        if (marketValue <= 0 || marketValue > 1_000_000_000_000_000m)
            throw new ApplicationCaseException("PROPERTY_VALUATION_AMOUNT_INVALID", 400);
        if (effectiveDate < new DateOnly(1900, 1, 1) || effectiveDate > today.AddDays(1))
            throw new ApplicationCaseException("PROPERTY_VALUATION_DATE_INVALID", 400);
    }

    public static void ExpectedVersion(int value)
    {
        if (value < 1) throw new ApplicationCaseException("PROPERTY_VALUATION_VERSION_INVALID", 400);
    }

    public static void RequireCurrent(PropertyValuerContext before, PropertyValuationContext current)
    {
        if (before.TenantId != current.TenantId || before.ActorId != current.ActorId ||
            before.PolicyVersion != current.PolicyVersion || before.SubjectVersion != current.SubjectVersion ||
            current.CustomerVersion < 1 || current.ConsentVersion < 1)
            throw new ApplicationCaseException("PROPERTY_VALUER_AUTHORITY_CHANGED", 409);
    }
}

public sealed class PropertyValuationApplication(TimeProvider clock) : IPropertyValuationApplication, IDisposable
{
    private const string Durability = "Temporary data";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ValuationRecord> valuations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> operations = new(StringComparer.Ordinal);

    public void AddFixture(string tenantId, string actorId, string customerId, string valuationId,
        string propertyReference, string propertyLabel,
        IReadOnlyList<(string Code, string Label, string Owner, string Reference, long Version)> evidence,
        DateTimeOffset assignedAt)
    {
        valuations.Add(Key(tenantId, valuationId), new(tenantId, actorId, customerId, valuationId,
            propertyReference, propertyLabel, "Assigned", 1, assignedAt,
            evidence.Select(value => new EvidenceRecord(value.Code, value.Label, value.Owner,
                value.Reference, value.Version, value.Version, null)).ToArray(), []));
    }

    public void ChangeEvidenceVersion(string tenantId, string valuationId, string evidenceCode, long version)
    {
        var key = Key(tenantId, valuationId);
        var row = valuations[key];
        var evidence = row.Evidence.ToArray();
        var index = Array.FindIndex(evidence, item => item.Code == evidenceCode);
        if (index < 0) throw new InvalidOperationException("Evidence fixture is missing.");
        evidence[index] = evidence[index] with { CurrentVersion = version };
        valuations[key] = row with { Evidence = evidence };
    }

    public async Task<IReadOnlyList<PropertyValuationView>> ListAsync(
        PropertyValuerContext context,
        Func<string, CancellationToken, Task<PropertyValuationContext>> authorize,
        CancellationToken ct)
    {
        ValuationRecord[] assigned;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            assigned = valuations.Values.Where(value => value.TenantId == context.TenantId &&
                value.AssignedActorId == context.ActorId).OrderBy(value => value.AssignedAt)
                .ThenBy(value => value.ValuationId, StringComparer.Ordinal).ToArray();
        }
        finally { gate.Release(); }
        var result = new List<PropertyValuationView>(assigned.Length);
        foreach (var row in assigned)
        {
            var authority = await authorize(row.CustomerId, ct).ConfigureAwait(false);
            PropertyValuationPolicy.RequireCurrent(context, authority);
            result.Add(View(row));
        }
        return result;
    }

    public async Task<IReadOnlyList<CustomerPropertyValuationView>> ListCustomerResultsAsync(
        CaseActorContext customer, CancellationToken ct)
    {
        if (customer.CustomerId is null)
            throw new ApplicationCaseException("CUSTOMER_CASE_ACCESS_REQUIRED", 403);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return valuations.Values.Where(value => value.TenantId == customer.TenantId &&
                value.CustomerId == customer.CustomerId && value.Revisions.Count > 0)
                .OrderBy(value => value.AssignedAt).ThenBy(value => value.ValuationId, StringComparer.Ordinal)
                .Select(CustomerView).ToArray();
        }
        finally { gate.Release(); }
    }

    public Task<PropertyValuationView> InspectEvidenceAsync(
        string valuationId, string evidenceCode, PropertyValuerContext before,
        InspectPropertyEvidence request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct) =>
        MutateAsync(valuationId, before, request.OperationId, request.ExpectedVersion, "EVIDENCE_INSPECTION",
            PropertyValuationPolicy.Identifier(evidenceCode, "PROPERTY_EVIDENCE_CODE_INVALID", 64),
            null, confirm, ct);

    public Task<PropertyValuationView> RecordValuationAsync(
        string valuationId, PropertyValuerContext before, RecordPropertyValuation request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct)
    {
        PropertyValuationPolicy.Request(request.ExpectedVersion, request.MarketValue,
            request.EffectiveDate, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        var normalized = request with
        {
            Currency = PropertyValuationPolicy.Currency(request.Currency),
            Basis = PropertyValuationPolicy.Text(request.Basis, "PROPERTY_VALUATION_BASIS_INVALID", 3, 500),
            Findings = PropertyValuationPolicy.Text(request.Findings, "PROPERTY_VALUATION_FINDINGS_INVALID", 3, 2000),
        };
        return MutateAsync(valuationId, before, request.OperationId, request.ExpectedVersion,
            "VALUATION", null, normalized, confirm, ct);
    }

    private async Task<PropertyValuationView> MutateAsync(
        string valuationId, PropertyValuerContext before, string operationId, int expectedVersion,
        string action, string? evidenceCode, RecordPropertyValuation? request,
        Func<string, CancellationToken, Task<PropertyValuationContext>> confirm, CancellationToken ct)
    {
        PropertyValuationPolicy.Identifier(valuationId, "PROPERTY_VALUATION_ID_INVALID");
        PropertyValuationPolicy.Operation(operationId);
        PropertyValuationPolicy.ExpectedVersion(expectedVersion);
        var fingerprint = Fingerprint(action, evidenceCode, request);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var operationKey = before.TenantId + ":" + before.ActorId + ":" + operationId;
            if (operations.TryGetValue(operationKey, out var replay))
            {
                if (replay.ValuationId != valuationId || replay.Action != action ||
                    replay.RequestFingerprint != fingerprint)
                    throw new ApplicationCaseException("OPERATION_REUSE_CONFLICT", 409);
                return View(Assigned(before, valuationId));
            }
            var row = Assigned(before, valuationId);
            if (row.Version != expectedVersion)
                throw new ApplicationCaseException("PROPERTY_VALUATION_VERSION_CHANGED", 409);
            var authority = await confirm(row.CustomerId, ct).ConfigureAwait(false);
            PropertyValuationPolicy.RequireCurrent(before, authority);
            var now = clock.GetUtcNow();
            var evidence = row.Evidence.ToArray();
            var revisions = row.Revisions.ToList();
            if (action == "EVIDENCE_INSPECTION")
            {
                var index = Array.FindIndex(evidence, value => value.Code == evidenceCode);
                if (index < 0) throw new ApplicationCaseException("PROPERTY_EVIDENCE_NOT_FOUND", 404);
                if (evidence[index].AssignedVersion != evidence[index].CurrentVersion)
                    throw new ApplicationCaseException("PROPERTY_EVIDENCE_VERSION_CHANGED", 409);
                if (evidence[index].InspectedAt is not null)
                    throw new ApplicationCaseException("PROPERTY_EVIDENCE_ALREADY_INSPECTED", 409);
                evidence[index] = evidence[index] with { InspectedAt = now };
            }
            else
            {
                if (evidence.Any(value => value.InspectedAt is null))
                    throw new ApplicationCaseException("PROPERTY_INSPECTION_INCOMPLETE", 409);
                if (evidence.Any(value => value.AssignedVersion != value.CurrentVersion))
                    throw new ApplicationCaseException("PROPERTY_EVIDENCE_VERSION_CHANGED", 409);
                revisions.Add(new(revisions.Count + 1, request!.MarketValue, request.Currency,
                    request.EffectiveDate, request.Basis, request.Findings, now, before.ActorId,
                    authority.PolicyVersion, authority.SubjectVersion, authority.CustomerVersion,
                    authority.ConsentVersion, evidence.Select(value => new PropertyValuationEvidenceSnapshot(
                        value.Code, value.Owner, value.Reference, value.CurrentVersion)).ToArray()));
            }
            var updated = row with
            {
                State = action == "VALUATION" ? "Valued" : "Inspection in progress",
                Version = row.Version + 1,
                Evidence = evidence,
                Revisions = revisions,
            };
            valuations[Key(before.TenantId, valuationId)] = updated;
            operations.Add(operationKey, new(valuationId, action, fingerprint));
            return View(updated);
        }
        finally { gate.Release(); }
    }

    private ValuationRecord Assigned(PropertyValuerContext context, string valuationId)
    {
        if (!valuations.TryGetValue(Key(context.TenantId, valuationId), out var row) ||
            row.AssignedActorId != context.ActorId)
            throw new ApplicationCaseException("PROPERTY_VALUATION_NOT_FOUND", 404);
        return row;
    }

    private static PropertyValuationView View(ValuationRecord row) => new(row.ValuationId,
        row.PropertyReference, row.PropertyLabel, row.State, row.Version, row.AssignedAt,
        row.Evidence.Select(value => new PropertyEvidenceView(value.Code, value.Label, value.Owner,
            value.Reference, value.AssignedVersion, value.CurrentVersion, value.InspectedAt is not null,
            value.InspectedAt)).ToArray(), row.Revisions, Durability);

    private static CustomerPropertyValuationView CustomerView(ValuationRecord row)
    {
        var latest = row.Revisions[^1];
        return new(row.ValuationId, row.PropertyReference, row.PropertyLabel, latest.Revision,
            latest.MarketValue, latest.Currency, latest.EffectiveDate, latest.Basis, latest.Findings,
            latest.RecordedAt, latest.RecordedBy, latest.Evidence.Count, Durability);
    }

    private static string Key(string tenantId, string valuationId) => tenantId + ":" + valuationId;
    internal static string Fingerprint(string action, string? evidenceCode, RecordPropertyValuation? request)
    {
        var canonical = request is null
            ? action + "\n" + evidenceCode
            : string.Join('\n', action, request.MarketValue.ToString(CultureInfo.InvariantCulture),
                request.Currency, request.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                request.Basis, request.Findings);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose() => gate.Dispose();

    private sealed record EvidenceRecord(string Code, string Label, string Owner, string Reference,
        long AssignedVersion, long CurrentVersion, DateTimeOffset? InspectedAt);
    private sealed record ValuationRecord(string TenantId, string AssignedActorId, string CustomerId,
        string ValuationId, string PropertyReference, string PropertyLabel, string State, int Version,
        DateTimeOffset AssignedAt, IReadOnlyList<EvidenceRecord> Evidence,
        List<PropertyValuationRevisionView> Revisions);
    private sealed record OperationRecord(string ValuationId, string Action, string RequestFingerprint);
}
