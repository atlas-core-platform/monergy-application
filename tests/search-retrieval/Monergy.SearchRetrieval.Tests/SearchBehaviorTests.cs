using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.SearchRetrieval.Domain;
using Monergy.Services.SearchRetrieval.Infrastructure;
using Xunit;

namespace Monergy.SearchRetrieval.Tests;

public sealed class SearchBehaviorTests
{
    [Fact]
    public async Task DocumentSearchReturnsOnlyAuthorizedDerivedEvidenceReferences()
    {
        var context = new SearchTestContext();
        var result = await context.Application.SearchDocumentsAsync(context.Request(SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-a", "salary evidence", SearchMatchMode.Lexical)));
        Assert.Equal(ContractOutcome.Success, result.Outcome);
        var hit = Assert.Single(result.Data!.Items);
        Assert.Equal("document-a", hit.SearchRecordId);
        Assert.Equal(SearchResultType.Document, hit.ResultType);
        Assert.Equal(SearchAuthority.EvidenceService, hit.AuthoritativeOwner);
        Assert.Equal(SearchAuthority.DerivedRebuildable, hit.RepresentationState);
        Assert.Equal("document-version-a", hit.Source.SourceObjectId);
        Assert.Equal("evidence-provenance-a", hit.Source.ProvenanceReferenceId);
    }

    [Fact]
    public async Task FinancialSearchPreservesFinancialProvenanceAndCalculationLineage()
    {
        var context = new SearchTestContext();
        var result = await context.Application.SearchFinancialDataAsync(context.Request(SearchContractNames.SearchFinancialData,
            new SearchFinancialData("customer-a", "savings calculation", SearchMatchMode.Semantic)));
        Assert.Equal(ContractOutcome.Success, result.Outcome);
        Assert.Contains(result.Data!.Items, hit => hit.ResultType == SearchResultType.Calculation &&
            hit.Source.ProvenanceReferenceId == "financial-provenance-a" &&
            hit.Source.CalculationLineageReferenceId == "lineage-a" &&
            hit.AuthoritativeOwner == SearchAuthority.FinancialRulesService);
    }

    [Fact]
    public void SemanticRankingIsDeterministicAndPreservesSourceIdentity()
    {
        var context = new SearchTestContext();
        var types = ImmutableHashSet.Create(SearchResultType.Document, SearchResultType.Financial, SearchResultType.Calculation);
        var first = context.Index.Search("customer-a", "authorization-a", types, "salary", SearchMatchMode.Semantic, 20);
        var second = context.Index.Search("customer-a", "authorization-a", types, "salary", SearchMatchMode.Semantic, 20);
        Assert.Equal(first.Select(item => (item.Record.SearchRecordId, item.Score)),
            second.Select(item => (item.Record.SearchRecordId, item.Score)));
        Assert.Equal("financial-a", first[0].Record.SearchRecordId);
        Assert.All(first, result => Assert.False(string.IsNullOrWhiteSpace(result.Record.Source.SourceReferenceId)));
    }

    [Fact]
    public async Task RebuildIsRepeatableAndUsesSourceOwnedProjections()
    {
        var context = new SearchTestContext();
        var first = context.Index.Rebuild(context.Source.ReadAll());
        var second = context.Index.Rebuild(context.Source.ReadAll().Reverse());
        Assert.Equal(first, second);
        var expected = context.Index.RebuildCustomer("customer-a", context.Source.ReadForCustomer("customer-a"));
        var refresh = await context.Application.RefreshAsync(context.Request(SearchContractNames.IndexRefreshRequested,
            new IndexRefreshRequested("customer-a")));
        Assert.Equal(expected, refresh.Data);
        Assert.Equal(SearchAuthority.DerivedRebuildable, refresh.Data!.RepresentationState);
    }

    [Fact]
    public async Task CustomerRefreshPreservesOtherPartitionsAndReturnsOnlyCustomerScopedEvidence()
    {
        var context = new SearchTestContext();
        var customerARecords = context.Records.Where(item => item.CustomerId == "customer-a").ToImmutableArray();
        var originalCustomerB = Assert.Single(context.Records, item => item.CustomerId == "customer-b");

        context.Source.Replace(customerARecords);
        var firstRefresh = await context.Application.RefreshAsync(context.Request(
            SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")));

        Assert.Equal(ContractOutcome.Success, firstRefresh.Outcome);
        Assert.Equal(customerARecords.Length, firstRefresh.Data!.RecordCount);

        var changedCustomerB = originalCustomerB with
        {
            Title = "Changed customer B projection",
            SearchableText = "content unavailable to salary search",
        };
        context.Source.Replace(customerARecords.Append(changedCustomerB));
        var secondRefresh = await context.Application.RefreshAsync(context.Request(
            SearchContractNames.IndexRefreshRequested, new IndexRefreshRequested("customer-a")));

        Assert.Equal(customerARecords.Length, secondRefresh.Data!.RecordCount);
        Assert.Equal(firstRefresh.Data.RebuildFingerprint, secondRefresh.Data.RebuildFingerprint);

        var customerBSearch = await context.Application.SearchDocumentsAsync(context.Request(
            SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-b", "salary", SearchMatchMode.Lexical),
            context.CustomerB));
        var customerBResult = Assert.Single(customerBSearch.Data!.Items);
        Assert.Equal(originalCustomerB.Title, customerBResult.Title);

        var customerASearch = await context.Application.SearchDocumentsAsync(context.Request(
            SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)));
        Assert.DoesNotContain(customerASearch.Data!.Items, item => item.SearchRecordId == originalCustomerB.SearchRecordId);

        var crossCustomer = await context.Application.SearchDocumentsAsync(context.Request(
            SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-b", "salary", SearchMatchMode.Lexical),
            context.CustomerA));
        Assert.Equal(ContractOutcome.Rejected, crossCustomer.Outcome);
    }

    [Fact]
    public async Task CustomerAndAuthorizationBoundariesPreventLeakage()
    {
        var context = new SearchTestContext();
        var customerA = await context.Application.SearchDocumentsAsync(context.Request(SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-a", "evidence", SearchMatchMode.Lexical)));
        Assert.DoesNotContain(customerA.Data!.Items, item => item.SearchRecordId is "document-a-hidden" or "document-b");
        var mismatched = await context.Application.SearchDocumentsAsync(context.Request(SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-b", "salary", SearchMatchMode.Lexical), context.CustomerA));
        Assert.Equal(ContractOutcome.Rejected, mismatched.Outcome);
        Assert.Equal(ContractErrorCategory.ValidationError, mismatched.Error!.Category);
    }

    [Fact]
    public async Task UnauthorizedProvenanceCannotBeRetrievedIndirectly()
    {
        var context = new SearchTestContext();
        var result = await context.Application.BuildRetrievalContextAsync(context.Request(SearchContractNames.BuildRetrievalContext,
            new BuildRetrievalContext("customer-a", ["document-a-hidden"])));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal(ContractErrorCategory.AccessDenied, result.Error!.Category);
        Assert.DoesNotContain("hidden", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedAndUnsupportedContractsAreRejected()
    {
        var context = new SearchTestContext();
        var invalid = await context.Application.SearchDocumentsAsync(context.Request(SearchContractNames.SearchDocuments,
            new SearchDocuments("customer-a", " ", SearchMatchMode.Lexical, 0)));
        Assert.Equal(ContractErrorCategory.ValidationError, invalid.Error!.Category);
        var unsupported = await context.Application.SearchDocumentsAsync(context.Request("WrongName",
            new SearchDocuments("customer-a", "salary", SearchMatchMode.Lexical)));
        Assert.Equal(ContractErrorCategory.UnsupportedOperation, unsupported.Error!.Category);
    }

    [Fact]
    public void DerivedIndexRejectsSearchAsAuthoritativeOwner()
    {
        var context = new SearchTestContext();
        var invalid = context.Records[0] with { AuthoritativeOwner = "Search & Retrieval Service" };
        Assert.Throws<ArgumentException>(() => context.Index.Rebuild([invalid]));
    }

    [Theory]
    [InlineData("DEV")]
    [InlineData("QA")]
    [InlineData("UAT")]
    [InlineData("PRODUCTION")]
    public void ReferenceAdaptersFailClosedOutsideLocalAndCi(string zone)
    {
        var configuration = SearchTestContext.BuildConfiguration(zone);
        Assert.Throws<InvalidOperationException>(() => new InMemoryDerivedSearchIndex(configuration));
        Assert.Throws<InvalidOperationException>(() => ReferenceAdapterGuard.EnsureAllowed(configuration));
    }
}
