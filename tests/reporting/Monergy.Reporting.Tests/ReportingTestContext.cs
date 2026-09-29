using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Domain;
using Monergy.Services.Reporting.Infrastructure;

namespace Monergy.Reporting.Tests;

internal sealed class ReportingTestContext
{
    public ReportingTestContext()
    {
        Configuration = BuildConfiguration("LOCAL");
        CustomerA = Security("customer-a", "authorization-a", "actor-a", "workload-a");
        CustomerB = Security("customer-b", "authorization-b", "actor-b", "workload-b");
        var snapshots = new[] { Snapshot("customer-a", 125000m), Snapshot("customer-b", 98000m) };
        Source = new CountingReportSourceReader(new ReferenceReportSourceReader(Configuration, snapshots));
        Repository = new InMemoryReportRepository(Configuration);
        Authorization = new ReferenceReportingAuthorizationPolicy(Configuration);
        Authorization.Grant(CustomerA);
        Authorization.Grant(CustomerB);
        Clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-28T10:15:00Z", null,
            System.Globalization.DateTimeStyles.RoundtripKind));
        Application = new ReportingApplication(Source, Repository, Authorization, Clock);
    }

    public IConfiguration Configuration { get; }
    public TrustedSecurityContext CustomerA { get; }
    public TrustedSecurityContext CustomerB { get; }
    public CountingReportSourceReader Source { get; }
    public InMemoryReportRepository Repository { get; }
    public ReferenceReportingAuthorizationPolicy Authorization { get; }
    public InMemoryReportRepository Evidence => Repository;
    public TestTimeProvider Clock { get; }
    public ReportingApplication Application { get; }

    public ContractRequest<T> Request<T>(string name, T payload, TrustedSecurityContext? security = null,
        string? idempotencyKey = "report-key") =>
        new(name, ContractGuard.CurrentVersion, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            null, security ?? CustomerA, idempotencyKey, payload);

    public static IConfiguration BuildConfiguration(string zone) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true",
            ["Monergy:ExecutionZone"] = zone,
        }).Build();

    private static ReportSourceSnapshot Snapshot(string customer, decimal income) => new(customer,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z", null, System.Globalization.DateTimeStyles.RoundtripKind),
        ImmutableArray.Create(
            new ReportLineItem("Monthly income", income, "INR", new("FinancialFact", $"fact-{customer}",
                ReportingAuthority.FinancialProfileService, $"evidence-{customer}", $"provenance-{customer}", null)),
            new ReportLineItem("Savings ratio", 0.36m, "RATIO", new("FinancialCalculation", $"calculation-{customer}",
                ReportingAuthority.FinancialRulesService, null, $"provenance-{customer}", $"lineage-{customer}"))));

    private static TrustedSecurityContext Security(string customer, string authorization, string actor, string workload) =>
        new(new(actor, "HUMAN", DateTimeOffset.UnixEpoch, $"authentication-{actor}"),
            new("reporting", workload), new("REFERENCE_REPORTING", null, authorization, customer));
}

internal sealed class CountingReportSourceReader(IReportSourceReader inner) : IReportSourceReader
{
    private int readCount;

    public int ReadCount => Volatile.Read(ref readCount);

    public Task<ReportSourceSnapshot?> ReadAsync(string customerId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref readCount);
        return inner.ReadAsync(customerId, cancellationToken);
    }
}

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
