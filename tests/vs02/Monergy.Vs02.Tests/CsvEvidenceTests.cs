using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Monergy.Services.Evidence;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class CsvEvidenceTests
{
    private const string Header = "Category,Description,Amount,Currency,Date,Source\r\n";
    private const string Row = "INCOME,October salary,125000.50,INR,2026-10-01,Salary statement\r\n";

    [Fact]
    public async Task QuotedRecordsBomAndReorderedHeadersPreserveValuesAndExactSource()
    {
        var content = Source("\uFEFFSource,DATE,Currency,Amount,Description,Category\r\n" +
            "\"Statement, page \"\"one\"\"\",2026-10-01,inr,125000.50,\"October\r\nsalary\",income\r\n");
        var facts = await new StructuredDocumentExtractor().ExtractAsync(content, "processing-csv", default);
        var fact = Assert.Single(facts);
        Assert.Equal("October\r\nsalary", fact.Label);
        Assert.Equal(125000.50m, fact.CandidateValue);
        Assert.Equal("INR", fact.Currency);
        Assert.Equal("INCOME", fact.FactType);
        Assert.Equal("CSV record 2: Statement, page \"one\"", fact.SourceLocation);
        Assert.Equal(content.Reference.CustomerId, fact.CustomerId);
        Assert.Equal(content.Reference.DocumentVersionId, fact.DocumentVersionId);
        Assert.Equal(content.Reference.EvidenceId, fact.EvidenceId);
        Assert.Equal(CsvFinancialDocumentExtractor.ExtractionVersion, fact.ExtractionVersion);
        Assert.Equal(facts, await new StructuredDocumentExtractor().ExtractAsync(content, "processing-csv", default));
        var other = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(content with
        { Reference = content.Reference with { DocumentVersionId = "another-version" } }, "processing-csv", default));
        Assert.NotEqual(fact.SourceFactId, other.SourceFactId);
    }

    [Theory]
    [InlineData("INCOME")]
    [InlineData("EXPENSE")]
    [InlineData("LOAN")]
    [InlineData("ASSET")]
    [InlineData("INVESTMENT")]
    [InlineData("INSURANCE")]
    [InlineData("TAX")]
    public async Task CopiesSevenExplicitCategoriesWithoutAssessmentOrCalculation(string category)
    {
        var fact = Assert.Single(await new StructuredDocumentExtractor().ExtractAsync(
            Source(Header + Row.Replace("INCOME", category, StringComparison.Ordinal)), "csv-categories", default));
        Assert.Equal(category, fact.FactType);
        Assert.Equal(125000.50m, fact.CandidateValue);
    }

    [Theory]
    [InlineData("Category,Description,Amount,Currency,Date,Source\n")]
    [InlineData("name,salary\nSomeone,125000\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Category\nINCOME,A,1,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,-1,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,1,INR,2026-02-30,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,=1+2,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,0.123456789012345678901234567890,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,1e3,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nUNKNOWN,A,1,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,1,123,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,\"unclosed,1,INR,2026-10-01,B\n")]
    [InlineData("Category,Description,Amount,Currency,Date,Source\nINCOME,A,1,INR,2026-10-01,B,extra\n")]
    public async Task InvalidFileNeverReturnsTheEarlierValidRow(string invalid)
    {
        var csv = invalid.StartsWith(Header.TrimEnd(), StringComparison.Ordinal)
            ? Header + Row + invalid[(invalid.IndexOf('\n') + 1)..]
            : invalid;
        // Header-only is separately invalid; invalid later rows cannot publish an earlier valid subset.
        if (invalid == Header.Replace("\r", "", StringComparison.Ordinal)) csv = invalid;
        await Assert.ThrowsAsync<DocumentExtractionException>(() =>
            new StructuredDocumentExtractor().ExtractAsync(Source(csv), "invalid-csv", default));
    }

    [Fact]
    public async Task DuplicateLabelsAndOversizedBatchesFailWithoutPartialCandidates()
    {
        var extractor = new StructuredDocumentExtractor();
        await Assert.ThrowsAsync<DocumentExtractionException>(() =>
            extractor.ExtractAsync(Source(Header + Row + Row), "duplicate-csv", default));
        var rows = Enumerable.Range(1, 101).Select(index =>
            $"INCOME,Salary {index},1,INR,2026-10-01,Statement\n");
        await Assert.ThrowsAsync<DocumentExtractionException>(() =>
            extractor.ExtractAsync(Source(Header + string.Concat(rows)), "large-csv", default));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(Source(Header + Row), "cancelled-csv", cancelled.Token));
    }

    [Fact]
    public async Task EvidenceOwnerAndHttpReaderKeepCsvHashTenantAndCustomerBoundaries()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var intakeToken = new string('a', 64);
        var processingToken = new string('b', 64);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true", ["Monergy:ExecutionZone"] = "LOCAL",
            ["Monergy:ProductIntake:EvidenceToken"] = intakeToken,
            ["Monergy:ProductIntake:ProcessingToken"] = processingToken,
            ["Monergy:TenantBoundary:CustomerTenants:customer-001"] = "T001",
        });
        var store = new ReferenceEvidenceContentStore();
        var repository = new InMemoryEvidenceRepository();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IEvidenceRepository>(repository);
        await using var host = builder.Build();
        host.MapEvidenceReferenceIntake(builder.Configuration);
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Monergy-Product-Token", intakeToken);
        var bytes = Encoding.UTF8.GetBytes("\uFEFF" + Header + Row);
        using var staged = await client.PostAsJsonAsync("/internal/local/v1/evidence/content",
            new ReferenceEvidenceContentRequest(new string('c', 32), Convert.ToBase64String(bytes), "text/csv"));
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var descriptor = await staged.Content.ReadFromJsonAsync<EvidenceContentDescriptor>();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor?.Sha256);
        var owner = new EvidenceApplication(repository, store, new CapturingTelemetry(), TimeProvider.System);
        var created = await owner.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion, Vs02TestContext.CreateVersionPayload() with
            { OriginalFileName = "financial.csv", DeclaredContentType = "text/csv",
                ContentReference = descriptor!.Reference, ContentSha256 = descriptor.Sha256 }, "csv-create", "csv-create"));
        Assert.Equal(ContractOutcome.Success, created.Outcome);
        var configuration = new ConfigurationManager
        {
            ["Monergy:DocumentProcessing:EvidenceUrl"] = client.BaseAddress.AbsoluteUri,
            ["Monergy:DocumentProcessing:EvidenceToken"] = processingToken,
        };
        using var reader = new HttpEvidenceContentReader(configuration);
        var security = Vs02TestContext.Security() with
        { Workload = new WorkloadContext("Job Management Service", "job-management:T001") };
        var source = await reader.ReadAsync("document-version-001", "customer-001", security, "csv-read", default);
        Assert.Equal("\uFEFF" + Header + Row, source?.Content);
        Assert.Equal(descriptor.Sha256, source?.Reference.ContentSha256);
        var denied = security with { Workload = security.Workload! with { WorkloadIdentityId = "job-management:T002" } };
        await Assert.ThrowsAsync<EvidenceDependencyException>(() =>
            reader.ReadAsync("document-version-001", "customer-001", denied, "csv-denied", default));
        Assert.Null(await reader.ReadAsync("missing-version", "customer-001", security, "csv-missing", default));
        using var binary = await client.PostAsJsonAsync("/internal/local/v1/evidence/content",
            new ReferenceEvidenceContentRequest(new string('d', 32), "AAEC", "text/csv"));
        Assert.Equal(HttpStatusCode.BadRequest, binary.StatusCode);
        client.DefaultRequestHeaders.Clear();
        using var unauthorized = await client.PostAsJsonAsync("/internal/local/v1/evidence/content",
            new ReferenceEvidenceContentRequest(new string('e', 32), Convert.ToBase64String(bytes), "text/csv"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingPublishesCandidatesOnlyForAnEntireValidCsv(bool invalidTail)
    {
        var value = Header + Row + (invalidTail ? "TAX,Invalid,not-an-amount,INR,2026-10-01,Statement\n" : "");
        var store = new ReferenceEvidenceContentStore();
        var descriptor = store.Stage(new string('f', 32), Encoding.UTF8.GetBytes(value), "text/csv");
        var telemetry = new CapturingTelemetry();
        var evidence = new EvidenceApplication(new InMemoryEvidenceRepository(), store, telemetry, TimeProvider.System);
        var created = await evidence.CreateDocumentVersionAsync(Vs02TestContext.Request(
            Vs02ContractNames.CreateDocumentVersion, Vs02TestContext.CreateVersionPayload() with
            { OriginalFileName = "financial.csv", DeclaredContentType = "text/csv",
                ContentReference = descriptor.Reference, ContentSha256 = descriptor.Sha256 }, "flow-csv", "flow-csv"));
        Assert.Equal(ContractOutcome.Success, created.Outcome);
        var repository = new InMemoryDocumentProcessingRepository();
        var processing = new DocumentProcessingApplication(repository, new SerializedEvidenceReader(evidence, store),
            new StructuredDocumentExtractor(), new ReferenceExecutionPolicy(), telemetry, TimeProvider.System);
        var result = await processing.ProcessDocumentAsync(Vs02TestContext.Request(
            Vs02ContractNames.ProcessDocument,
            new ProcessDocument("processing-csv", "document-version-001", "evidence-001",
                Vs02TestContext.CustomerId, DateTimeOffset.UtcNow), "flow-process", "flow-process"));
        var record = await repository.GetAsync("processing-csv", default);
        Assert.Equal(invalidTail ? ProcessingState.Failed : ProcessingState.Completed, record?.State);
        Assert.Equal(invalidTail ? 0 : 1, record?.Facts.Count);
        if (invalidTail) Assert.Equal("processing.extraction.failed", result.Error?.Code);
        else Assert.Equal(descriptor.Sha256, (await evidence.GetEvidenceReferenceAsync(Vs02TestContext.Request(
            Vs02ContractNames.GetEvidenceReference, new GetEvidenceReference("document-version-001", Vs02TestContext.CustomerId),
            "flow-source"))).Data?.ContentSha256);
    }

    private static EvidenceContent Source(string value) => new(new EvidenceReference(
        "evidence-csv", "document-csv", "version-csv", "customer-001", "reference://csv",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))), "text/csv"), value);
}
