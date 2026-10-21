using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Domain;
using Xunit;

namespace Monergy.SearchRetrieval.Tests;

public sealed class IndexFreshnessTests
{
    [Fact]
    public async Task RetrievalRejectsRemovedSelectionInsteadOfUsingEarlierSearch()
    {
        var setup = new SearchTestContext();
        var source = new CurrentSource(setup.Records.Where(x => x.CustomerId == "customer-a" && x.RequiredAuthorizationContextId == "authorization-a").ToImmutableArray());
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        var before = await app.SearchDocumentsAsync(setup.Request(SearchContractNames.SearchDocuments, new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)));
        Assert.Single(before.Data!.Items);
        source.Records = [];
        var after = await app.BuildRetrievalContextAsync(setup.Request(SearchContractNames.BuildRetrievalContext, new BuildRetrievalContext("customer-a", ["document-a"])));
        Assert.Equal(ContractOutcome.Rejected, after.Outcome);
        Assert.Equal("search.context.access-denied", after.Error!.Code);
    }

    [Fact]
    public async Task RetrievalAndRefreshUseReplacedOwnerDataWithoutPriorSearch()
    {
        var setup = new SearchTestContext();
        var changed = setup.Records[0] with { Title = "Corrected statement", Source = setup.Records[0].Source with { SourceObjectId = "version-replacement" } };
        var source = new CurrentSource([changed]);
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        var context = await app.BuildRetrievalContextAsync(setup.Request(SearchContractNames.BuildRetrievalContext, new BuildRetrievalContext("customer-a", ["document-a"])));
        var hit = Assert.Single(context.Data!.Items);
        Assert.Equal("Corrected statement", hit.Title);
        Assert.Equal("version-replacement", hit.Source.SourceObjectId);
        var first = await app.RefreshAsync(setup.Request(SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")));
        Assert.Equal(1, first.Data!.RecordCount);
        source.Records = [];
        var second = await app.RefreshAsync(setup.Request(SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")));
        Assert.Equal(0, second.Data!.RecordCount);
        Assert.NotEqual(first.Data.RebuildFingerprint, second.Data.RebuildFingerprint);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("context")]
    [InlineData("refresh")]
    public async Task OwnerFailureNeverFallsBackToCachedData(string operation)
    {
        var setup = new SearchTestContext();
        var source = new CurrentSource([]) { Fail = true };
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        ContractOutcome outcome = operation switch
        {
            "search" => (await app.SearchDocumentsAsync(setup.Request(SearchContractNames.SearchDocuments, new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)))).Outcome,
            "context" => (await app.BuildRetrievalContextAsync(setup.Request(SearchContractNames.BuildRetrievalContext, new BuildRetrievalContext("customer-a", ["document-a"])))).Outcome,
            _ => (await app.RefreshAsync(setup.Request(SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")))).Outcome,
        };
        Assert.Equal(ContractOutcome.Rejected, outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForgedCustomerOrAuthorizationSnapshotFailsClosed(bool customer)
    {
        var setup = new SearchTestContext();
        var record = setup.Records[0];
        var source = new CurrentSource([customer ? record with { CustomerId = "customer-b" } : record with { RequiredAuthorizationContextId = "authorization-b" }]);
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        var result = await app.SearchDocumentsAsync(setup.Request(SearchContractNames.SearchDocuments, new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal("search.source.boundary-invalid", result.Error!.Code);
    }

    [Fact]
    public async Task ConcurrentOwnerReadsKeepTheirOwnSnapshots()
    {
        var setup = new SearchTestContext();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CurrentSource([setup.Records[0]]);
        var count = 0;
        source.BeforeRead = async () => { if (Interlocked.Increment(ref count) == 1) { firstStarted.SetResult(); await release.Task; } };
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        var request = setup.Request(SearchContractNames.SearchDocuments, new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical));
        var first = app.SearchDocumentsAsync(request);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Records = [setup.Records[0] with { Title = "Replacement salary" }];
        var second = await app.SearchDocumentsAsync(request);
        release.SetResult();
        Assert.Equal("Replacement salary", Assert.Single(second.Data!.Items).Title);
        Assert.Equal("Annual salary statement", Assert.Single((await first).Data!.Items).Title);
        Assert.Equal("Annual salary statement", Assert.Single(setup.Index.FindAuthorized("customer-a", "authorization-a", ["document-a"])).Title);
    }

    [Fact]
    public async Task AuthorizationLossDuringOwnerReadRejectsTheSnapshot()
    {
        var setup = new SearchTestContext();
        var policy = new RevocablePolicy();
        var source = new CurrentSource([setup.Records[0]]) { BeforeRead = () => { policy.Allowed = false; return Task.CompletedTask; } };
        var app = new SearchRetrievalApplication(setup.Index, source, policy);
        var result = await app.SearchDocumentsAsync(setup.Request(SearchContractNames.SearchDocuments, new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task CancellationAfterOwnerReadDoesNotReturnASnapshot()
    {
        var setup = new SearchTestContext();
        using var cancel = new CancellationTokenSource();
        var source = new CurrentSource([setup.Records[0]]) { BeforeRead = () => { cancel.Cancel(); return Task.CompletedTask; } };
        var app = new SearchRetrievalApplication(setup.Index, source, setup.Authorization);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.RefreshAsync(setup.Request(SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")), cancel.Token));
    }

    private sealed class RevocablePolicy : ISearchAuthorizationPolicy
    {
        public bool Allowed { get; set; } = true;
        public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<ContractError?>(Allowed ? null : new("revoked", ContractErrorCategory.AccessDenied, "Current authority required", false, correlationId));
    }

    private sealed class CurrentSource(ImmutableArray<DerivedSearchRecord> records) : ISearchProjectionSource, ICustomerSearchProjectionSource
    {
        public ImmutableArray<DerivedSearchRecord> Records { get; set; } = records;
        public bool Fail { get; set; }
        public Func<Task>? BeforeRead { get; set; }
        public ImmutableArray<DerivedSearchRecord> ReadAll() => throw new InvalidOperationException("Stale read");
        public ImmutableArray<DerivedSearchRecord> ReadForCustomer(string customerId) => throw new InvalidOperationException("Stale read");
        public async Task<ImmutableArray<DerivedSearchRecord>> ReadCurrentCustomerAsync(string customerId, string authorizationContextId,
            TrustedSecurityContext security, string operation, string correlationId, CancellationToken ct)
        {
            var snapshot = Records;
            if (BeforeRead is not null) await BeforeRead();
            ct.ThrowIfCancellationRequested();
            if (Fail) throw new SearchSourceException(new("owner-unavailable", ContractErrorCategory.DependencyFailure, "Unavailable", true, correlationId));
            return snapshot;
        }
    }
}
