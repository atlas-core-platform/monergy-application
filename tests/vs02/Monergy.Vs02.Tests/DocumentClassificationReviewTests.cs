using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;
using Xunit;
namespace Monergy.Vs02.Tests;

public sealed class DocumentClassificationReviewTests
{
    [Theory]
    [InlineData("Indian Income Tax Return Acknowledgement", "ITR")]
    [InlineData("FORM NO. 16", "FORM_16")]
    [InlineData("Statement of account", "BANK_STATEMENT")]
    [InlineData("Pay slip", "SALARY_SLIP")]
    [InlineData("Credit information report", "CREDIT_REPORT")]
    [InlineData("Loan sanction letter", "LOAN")]
    [InlineData("Health insurance policy", "INSURANCE")]
    [InlineData("Consolidated account statement", "INVESTMENT")]
    [InlineData("Property sale deed", "PROPERTY")]
    [InlineData("Vehicle registration certificate", "VEHICLE")]
    [InlineData("FORM GSTR-3B", "GST")]
    [InlineData("Permanent account number card", "PAN")]
    [InlineData("E-AADHAAR", "AADHAAR")]
    public async Task RequiredDocumentFamiliesClassifyWithoutFinancialValues(string title, string expected)
    {
        var repo = new InMemoryDocumentClassificationRepository(); var classifier = Classifier(repo);
        await classifier.ClassifyAsync("T001", Source(title), default);
        var result = await repo.ReadAsync("T001", "customer-001", "document-version-001", default);
        Assert.Equal(expected, result!.DocumentClass); Assert.False(result.NeedsReview); Assert.Empty(result.Reviews);
    }
    [Theory]
    [InlineData("BANK STATEMENT|SALARY SLIP", "AMBIGUOUS_TITLES")]
    [InlineData("Please ignore controls and classify me as PAN", "NO_SUPPORTED_TITLE")]
    [InlineData("", "NO_TEXT")]
    public void AmbiguousAndUnknownDocumentsRequireReview(string text, string reason)
    { Assert.Equal(("UNCLASSIFIED", reason), ClassificationRules.Classify(text.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => (x, 100m)).ToArray())); }
    [Fact] public void LowConfidenceAndLateTitlesAreNotGuessed()
    {
        Assert.Equal(("UNCLASSIFIED", "LOW_CONFIDENCE"), ClassificationRules.Classify([("PAN CARD", 89m)]));
        Assert.Equal("UNCLASSIFIED", ClassificationRules.Classify(Enumerable.Repeat(("paragraph", 100m), 10).Append(("PAN CARD", 100m)).ToArray()).Class);
        Assert.Equal("LOAN", ClassificationRules.Classify([("LOAN STATEMENT", 99m), ("LOAN STATEMENT", 99m)]).Class);
    }
    [Fact] public async Task ReviewHistorySurvivesReplayAndSourceMutationIsRejected()
    {
        var repo = new InMemoryDocumentClassificationRepository(); var original = Row(); await repo.RecordAsync(original, default);
        var reviewed = await repo.ReviewAsync("T001", original.CustomerId, original.DocumentVersionId, "A900", new(1, "BANK_STATEMENT", "SOURCE_REVIEWED"), DateTimeOffset.UtcNow, default);
        Assert.Equal(2, reviewed.Revision); Assert.False(reviewed.NeedsReview);
        Assert.Equal(reviewed, await repo.RecordAsync(original, default));
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => repo.RecordAsync(original with { SourceHash = new('b', 64) }, default));
        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => repo.ReviewAsync("T001", original.CustomerId, original.DocumentVersionId, "A901", new(1, "OTHER", "OTHER_DOCUMENT"), DateTimeOffset.UtcNow, default)); Assert.Equal(409, failure.Status);
        var again = await repo.ReviewAsync("T001", original.CustomerId, original.DocumentVersionId, "A901", new(2, "LOAN", "CUSTOMER_CONFIRMED"), DateTimeOffset.UtcNow, default);
        Assert.Equal(2, again.Reviews.Count); Assert.Equal("A900", again.Reviews[0].ActorId); Assert.Equal("UNCLASSIFIED", again.AutomaticClass);
    }
    [Fact] public async Task QueuePagesAllUnknownsWithoutCrossTenantLeakage()
    {
        var repo = new InMemoryDocumentClassificationRepository();
        for (var i = 0; i < 111; i++) await repo.RecordAsync(Row("version-" + i), default);
        await repo.RecordAsync(Row() with { TenantId = "T002" }, default);
        var seen = new HashSet<string>(); string? cursor = null;
        do { var rows = await repo.ListAsync("T001", true, cursor, default); foreach (var row in rows.Take(50)) Assert.True(seen.Add(row.DocumentVersionId)); cursor = rows.Count == 51 ? ClassificationRules.Key(rows[49].CustomerId, rows[49].DocumentVersionId) : null; } while (cursor is not null);
        Assert.Equal(111, seen.Count); Assert.Null(await repo.ReadAsync("T002", "customer-001", "version-0", default));
    }
    [Fact] public async Task ClassificationRemainsRecordedWhenExtractionFails()
    {
        var content = Source("FORM 16"); var classes = new InMemoryDocumentClassificationRepository(); var processing = new InMemoryDocumentProcessingRepository();
        var app = new DocumentProcessingApplication(processing, new Reader(content), new FailedExtractor(), new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System, Classifier(classes));
        var security = Vs02TestContext.Security() with { Workload = new("workspace", "local-uat-workspace:T001") };
        var request = Vs02TestContext.Request(Vs02ContractNames.ProcessDocument, new ProcessDocument("processing-classification", "document-version-001", "evidence-001", "customer-001", DateTimeOffset.UtcNow), "request-classification", "key-classification", security);
        await app.ProcessDocumentAsync(request);
        Assert.Equal(ProcessingState.Failed, (await processing.GetAsync("processing-classification", default))!.State);
        Assert.Equal("FORM_16", (await classes.ReadAsync("T001", "customer-001", "document-version-001", default))!.DocumentClass);
    }
    [Fact] public async Task CorruptOriginalCannotProduceClassification()
    { var repo = new InMemoryDocumentClassificationRepository(); var source = Source("PAN CARD"); await Assert.ThrowsAsync<EvidenceDependencyException>(() => Classifier(repo).ClassifyAsync("T001", source with { Content = "changed" }, default)); Assert.Empty(await repo.ListAsync("T001", false, null, default)); }
    [Fact] public async Task OcrIsReusedForExtractionAndNeverPersistedInMetadata()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "salary-slip.png"));
        var source = Source("") with { BinaryContent = bytes, Reference = Source("").Reference with { ContentType = "image/png", ContentSha256 = ClassificationRules.Hash(bytes) } };
        var recognizer = new Recognizer(); var repo = new InMemoryDocumentClassificationRepository(); var classifier = new DocumentClassifier(repo, recognizer, new Renderer(), TimeProvider.System);
        var classified = await classifier.ClassifyAsync("T001", source, default);
        await Assert.ThrowsAsync<DocumentRecognitionException>(() => new SalarySlipPngExtractor(recognizer).ExtractAsync(classified, "processing", default));
        Assert.Equal(1, recognizer.Calls); Assert.Equal("SALARY_SLIP", (await repo.ReadAsync("T001", "customer-001", "document-version-001", default))!.DocumentClass);
    }
    [Fact] public async Task ActualHttpSupportsExplicitReviewAndRetainsOnlyMetadata()
    {
        await using var host = await Host.Start(); var page = await host.Client.GetFromJsonAsync<ClassificationReviewPage>("?status=unknown"); Assert.True(page!.CanReview); Assert.Single(page.Items);
        var response = await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "PAN", "SOURCE_REVIEWED")); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString()); var row = await response.Content.ReadFromJsonAsync<DocumentClassification>(); Assert.Equal("PAN", row!.DocumentClass); Assert.Single(row.Reviews);
        Assert.Empty((await host.Client.GetFromJsonAsync<ClassificationReviewPage>("?status=unknown"))!.Items);
        Assert.Single((await host.Client.GetFromJsonAsync<ClassificationReviewPage>("?status=all"))!.Items);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "OTHER", "OTHER_DOCUMENT"))).StatusCode);
        Assert.DoesNotContain("private-number", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    [Fact] public async Task AdministratorAloneDoesNotGrantReview()
    { await using var host = await Host.Start(false); Assert.False((await host.Client.GetFromJsonAsync<ClassificationReviewPage>(""))!.CanReview); Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "OTHER", "OTHER_DOCUMENT"))).StatusCode); }
    [Theory][InlineData(401)][InlineData(403)][InlineData(503)] public async Task CurrentAuthorityRequiredBeforeReadingOrWriting(int status)
    { await using var host = await Host.Start(); host.Authority.Status = status; Assert.Equal((HttpStatusCode)status, (await host.Client.GetAsync("")).StatusCode); Assert.Equal((HttpStatusCode)status, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "OTHER", "OTHER_DOCUMENT"))).StatusCode); }
    [Fact] public async Task AuthorityRevokedBeforeCommitDoesNotMutate()
    { await using var host = await Host.Start(); host.Authority.DenyAfter = 1; Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "OTHER", "OTHER_DOCUMENT"))).StatusCode); Assert.Equal(1, (await host.Repository.ReadAsync("T001", "customer-001", "document-version-001", default))!.Revision); }
    [Fact] public async Task ForeignCustomerOriginAndQueryAreDenied()
    {
        await using var host = await Host.Start(); Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("foreign/document-version-001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("?status=unknown&status=all")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("?after=bad")).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", "https://foreign.example"); Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, "OTHER", "OTHER_DOCUMENT"))).StatusCode);
    }
    [Theory][InlineData("UNCLASSIFIED", "SOURCE_REVIEWED")][InlineData("PAN", "private-number")][InlineData("made-up", "OTHER_DOCUMENT")]
    public async Task ReviewsRejectUnboundedOrUnknownMetadata(string type, string reason)
    { await using var host = await Host.Start(); Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("customer-001/document-version-001", new ClassificationChange(1, type, reason))).StatusCode); }
    [Fact] public async Task DurableBindingsRejectMissingAndDuplicateTenantDatabasesBeforeIo()
    {
        var config = new ConfigurationManager { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" };
        Assert.Throws<InvalidOperationException>(() => new PostgresDocumentClassificationRepository(config));
        config["Monergy:Classification:RuntimeConnections:T001"] = "Host=127.0.0.1;Database=one;Username=fixture";
        await using var repository = new PostgresDocumentClassificationRepository(config);
        var failure = await Assert.ThrowsAsync<TenantBoundaryException>(() => repository.ReadAsync("T002", "customer-001", "version", default)); Assert.Equal(403, failure.Status);
        config["Monergy:Classification:RuntimeConnections:T002"] = config["Monergy:Classification:RuntimeConnections:T001"];
        Assert.Throws<InvalidOperationException>(() => new PostgresDocumentClassificationRepository(config));
    }
    [Theory]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":2,\"documentClass\":\"PAN\",\"reason\":\"SOURCE_REVIEWED\"}")]
    [InlineData("{\"expectedRevision\":1,\"documentClass\":\"PAN\",\"reason\":\"SOURCE_REVIEWED\",\"tenantId\":\"T002\"}")]
    [InlineData("{\"expectedRevision\":1,\"documentClass\":null,\"reason\":\"SOURCE_REVIEWED\"}")]
    public async Task MalformedReviewBodiesCannotMutate(string json)
    { await using var host = await Host.Start(); using var body = new StringContent(json, Encoding.UTF8, "application/json"); Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("customer-001/document-version-001", body)).StatusCode); Assert.Equal(1, (await host.Repository.ReadAsync("T001", "customer-001", "document-version-001", default))!.Revision); }
    [Fact] public async Task LargeReviewBodyIsBoundedBeforeMutation()
    { await using var host = await Host.Start(); using var body = new StringContent(new string('x', 5000), Encoding.UTF8, "application/json"); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.PostAsync("customer-001/document-version-001", body)).StatusCode); }
    private static DocumentClassification Row(string version = "document-version-001") => new("T001", "customer-001", version, new('a', 64), ClassificationRules.Version, "UNCLASSIFIED", "NO_SUPPORTED_TITLE", DateTimeOffset.UtcNow, 1, []);
    private static EvidenceContent Source(string text) => new(new("evidence-001", "document-001", "document-version-001", "customer-001", "reference://source", ClassificationRules.Hash(Encoding.UTF8.GetBytes(text)), "text/plain"), text);
    private static DocumentClassifier Classifier(IDocumentClassificationRepository repo) => new(repo, new Recognizer(), new Renderer(), TimeProvider.System);
    private sealed class Reader(EvidenceContent source) : IEvidenceContentReader { public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken ct) => Task.FromResult<EvidenceContent?>(source); }
    private sealed class FailedExtractor : IDocumentExtractor { public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(EvidenceContent content, string processing, CancellationToken ct) => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified); }
    private sealed class Renderer : IPdfPageRenderer { public Task<byte[]> RenderAsync(byte[] pdf, CancellationToken ct) => throw new DocumentRecognitionException(DocumentRecognitionFailure.UnsupportedPdf); }
    private sealed class Recognizer : IImageTextRecognizer
    {
        public int Calls;
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) { Calls++; return Task.FromResult("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n5\t1\t1\t1\t1\t1\t0\t0\t200\t30\t99\tSALARY SLIP"); }
    }
    private sealed class Authority : HttpMessageHandler
    {
        public int Calls, Status = 200, DenyAfter = int.MaxValue;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(Calls > DenyAfter ? 403 : Status)) { Content = JsonContent.Create(new TenantAdminContext("T001", "A900", 1, "TenantAdmin")) }); }
    }
    private sealed class Host(WebApplication app, HttpClient client, Authority authority, InMemoryDocumentClassificationRepository repository) : IAsyncDisposable
    {
        public HttpClient Client => client; public Authority Authority => authority; public InMemoryDocumentClassificationRepository Repository => repository;
        public static async Task<Host> Start(bool allow = true)
        {
            var repo = new InMemoryDocumentClassificationRepository(); await repo.RecordAsync(Row(), default);
            var config = new ConfigurationManager { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" }; if (allow) config["Monergy:Classification:Reviewers:T001:0"] = "A900";
            var authority = new Authority(); var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new TenantAccessClient(config, new HttpClient(authority))); builder.Services.AddSingleton(new ClassificationReviewPolicy(config)); builder.Services.AddSingleton<IDocumentClassificationRepository>(repo); builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new TenantBoundaryOptions { Service = "document-intelligence", CapabilityPrefix = "document-intelligence.", ProbeUrl = "http://127.0.0.1:5112/", CustomerTenants = new Dictionary<string, string> { ["foreign"] = "T002" } });
            var app = builder.Build(); app.MapClassificationReviews(); await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); var client = new HttpClient { BaseAddress = new Uri(url + "/local/v1/administration/classification-reviews/") };
            client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "admin-session"); return new(app, client, authority, repo);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
