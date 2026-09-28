using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Domain;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class ReferenceReportSourceReader : IReportSourceReader
{
    private readonly ImmutableDictionary<string, ReportSourceSnapshot> snapshots;

    public ReferenceReportSourceReader(IConfiguration configuration, IEnumerable<ReportSourceSnapshot>? values = null)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        snapshots = (values ?? DefaultSnapshots()).ToImmutableDictionary(item => item.CustomerId, StringComparer.Ordinal);
    }

    public Task<ReportSourceSnapshot?> ReadAsync(string customerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshots.TryGetValue(customerId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    private static IEnumerable<ReportSourceSnapshot> DefaultSnapshots()
    {
        const string customer = "reference-customer";
        yield return new(customer, DateTimeOffset.Parse("2026-09-01T00:00:00Z", null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        [
            new("Monthly income", 125000m, "INR", new("FinancialFact", "financial-fact-income-001",
                ReportingAuthority.FinancialProfileService, "evidence-income-001", "financial-provenance-income-001", null)),
            new("Monthly expenses", 80000m, "INR", new("FinancialFact", "financial-fact-expenses-001",
                ReportingAuthority.FinancialProfileService, "evidence-expenses-001", "financial-provenance-expenses-001", null)),
            new("Savings ratio", 0.36m, "RATIO", new("FinancialCalculation", "calculation-result-savings-001",
                ReportingAuthority.FinancialRulesService, null, "financial-provenance-savings-001", "calculation-lineage-savings-001")),
        ]);
    }
}

public sealed class InMemoryReportRepository(IConfiguration configuration) : IReportRepository, IDisposable
{
    private readonly Dictionary<string, TrustedFinancialReport> reports = new(StringComparer.Ordinal);
    private readonly Dictionary<ReportOperationIdentity, TrustedFinancialReport> operations = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object sync = new();
    private readonly bool allowed = Ensure(configuration);

    public async Task<ReportOperationResult> GetOrCreateAsync(
        ReportOperationIdentity identity,
        Func<CancellationToken, Task<ReportGenerationAttempt>> reportFactory,
        CancellationToken cancellationToken = default)
    {
        _ = allowed;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operations.TryGetValue(identity, out var committedReport))
            {
                return new(committedReport, null, false);
            }

            var attempt = await reportFactory(cancellationToken).ConfigureAwait(false);
            if (attempt.Report is null || attempt.Error is not null)
            {
                return new(null, attempt.Error, false);
            }

            lock (sync)
            {
                reports[$"{attempt.Report.CustomerId}|{attempt.Report.ReportId}"] = attempt.Report;
                operations[identity] = attempt.Report;
            }
            return new(attempt.Report, null, true);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public TrustedFinancialReport? Find(string customerId, string reportId)
    {
        lock (sync) return reports.GetValueOrDefault($"{customerId}|{reportId}");
    }

    public void Dispose() => operationGate.Dispose();

    private static bool Ensure(IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        return true;
    }
}

public sealed class ReferenceReportingAuthorizationPolicy(IConfiguration configuration) : IReportingAuthorizationPolicy
{
    private readonly Dictionary<string, TrustedSecurityContext> grants = new(StringComparer.Ordinal);
    private readonly bool allowed = Ensure(configuration);

    public void Grant(TrustedSecurityContext context) => grants[context.Access.AuthorizationContextId] = context;

    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        _ = allowed;
        cancellationToken.ThrowIfCancellationRequested();
        var permitted = grants.TryGetValue(context.Access.AuthorizationContextId, out var grant) && grant == context &&
            context.Access.CustomerId == customerId && operation is ReportingContractNames.GenerateReport or ReportingContractNames.GetReport;
        return Task.FromResult(permitted ? null : new ContractError("report.authorization.denied",
            ContractErrorCategory.AccessDenied, "Current customer, actor and workload authorization is required.",
            false, correlationId));
    }

    private static bool Ensure(IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        return true;
    }
}

public sealed class InMemoryReportEvidenceSink(IConfiguration configuration) : IReportEvidenceSink
{
    private readonly List<DomainEvent<ReportGeneratedPayload>> events = [];
    private readonly object sync = new();
    private readonly bool allowed = Ensure(configuration);
    public IReadOnlyList<DomainEvent<ReportGeneratedPayload>> Events
    {
        get { lock (sync) return events.ToArray(); }
    }
    public void Record(DomainEvent<ReportGeneratedPayload> generatedEvent)
    {
        _ = allowed;
        lock (sync) events.Add(generatedEvent);
    }
    private static bool Ensure(IConfiguration configuration) { ReferenceAdapterGuard.EnsureAllowed(configuration); return true; }
}
