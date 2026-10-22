using System.Net;
using System.Net.Http.Json;
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
public sealed class DocumentOcrTests
{
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
    private static string Tsv(string text, int confidence = 99) => Header + (text.Length == 0 ? "" : $"5\t1\t1\t1\t1\t1\t0\t0\t200\t30\t{confidence}\t{text}");
    private static EvidenceContent Source()
    { var bytes = "%PDF-1.7 fixture"u8.ToArray(); return new(new("evidence-001", "document-001", "document-version-001", "customer-001", "reference://source", ClassificationRules.Hash(bytes), "application/pdf"), "", bytes); }
    private static DocumentOcrResult Row() => new("T001", "customer-001", "document-version-001", Source().Reference.ContentSha256, DocumentOcrRules.Version, "AVAILABLE", DateTimeOffset.UtcNow, [new(1, [new(1, "Private document text", 99)])]);
    [Fact] public async Task AllPagesTranscribeUnknownDocumentsWithoutInventingFinancialFacts()
    {
        var repo = new InMemoryDocumentOcrRepository(); var renderer = new Renderer(2); var recognizer = new Recognizer(Tsv("Unfamiliar title"), Tsv("Second page text"));
        var result = await new DocumentOcrService(repo, recognizer, renderer, TimeProvider.System).RecognizeAsync("T001", Source(), default);
        Assert.Equal("AVAILABLE", result.Result.State); Assert.Equal(2, result.Result.Pages.Count); Assert.Null(result.SinglePageTsv);
        Assert.Equal("Second page text", result.Result.Pages[1].Lines[0].Text); Assert.Equal(2, recognizer.Calls);
        Assert.Equal(Source().Reference.ContentSha256, (await repo.ReadOcrAsync("T001", "customer-001", "document-version-001", default))!.SourceHash);
        Assert.Null(await repo.ReadOcrAsync("T002", "customer-001", "document-version-001", default));
    }
    [Theory][InlineData(89, "uncertain", "UNRELIABLE_TEXT")][InlineData(99, "", "NO_TEXT")][InlineData(99, "readable", "AVAILABLE")]
    public async Task ConfidenceAndEmptyTextRemainExplicit(int confidence, string text, string state)
    {
        var result = await new DocumentOcrService(new InMemoryDocumentOcrRepository(), new Recognizer(Tsv(text, confidence)), new Renderer(1), TimeProvider.System).RecognizeAsync("T001", Source(), default);
        Assert.Equal(state, result.Result.State); if (text.Length > 0) Assert.Equal(text, result.Result.Pages[0].Lines[0].Text);
    }
    [Fact] public async Task EmptyPageCannotBeSilentlySkipped()
    {
        var result = await new DocumentOcrService(new InMemoryDocumentOcrRepository(), new Recognizer(Tsv("first"), Header), new Renderer(2), TimeProvider.System).RecognizeAsync("T001", Source(), default);
        Assert.Equal("UNRELIABLE_TEXT", result.Result.State); Assert.Empty(result.Result.Pages[1].Lines);
    }
    [Theory][InlineData(0)][InlineData(21)] public async Task PageLimitsRejectBeforeOcr(int count)
    {
        var recognizer = new Recognizer(Tsv("never")); var result = await new DocumentOcrService(new InMemoryDocumentOcrRepository(), recognizer, new Renderer(count), TimeProvider.System).RecognizeAsync("T001", Source(), default);
        Assert.Equal("UNSUPPORTED", result.Result.State); Assert.Empty(result.Result.Pages); Assert.Equal(0, recognizer.Calls);
    }
    [Fact] public async Task EngineFailureDoesNotPublishPartialPages()
    {
        var repo = new InMemoryDocumentOcrRepository(); var recognizer = new Recognizer(Tsv("first")) { FailAfter = 1 };
        await Assert.ThrowsAsync<OcrEngineUnavailableException>(() => new DocumentOcrService(repo, recognizer, new Renderer(2), TimeProvider.System).RecognizeAsync("T001", Source(), default));
        Assert.Null(await repo.ReadOcrAsync("T001", "customer-001", "document-version-001", default));
    }
    [Fact] public async Task CorruptOriginalAndCancellationNeverInvokeOcr()
    {
        var recognizer = new Recognizer(Tsv("never")); var service = new DocumentOcrService(new InMemoryDocumentOcrRepository(), recognizer, new Renderer(1), TimeProvider.System);
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => service.RecognizeAsync("T001", Source() with { BinaryContent = [1, 2, 3] }, default));
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecognizeAsync("T001", Source(), cancelled.Token)); Assert.Equal(0, recognizer.Calls);
    }
    [Fact] public async Task SourceVersionCannotBeReboundAndReturnedCollectionsCannotMutateStorage()
    {
        var repo = new InMemoryDocumentOcrRepository(); var lines = new[] { new DocumentOcrLine(1, "original", 99) }; var row = Row() with { Pages = [new(1, lines)] };
        await repo.SaveAsync(row, default); lines[0] = new(1, "tampered", 99);
        Assert.Equal("original", (await repo.ReadOcrAsync("T001", "customer-001", "document-version-001", default))!.Pages[0].Lines[0].Text);
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => repo.SaveAsync(row with { SourceHash = new('b', 64) }, default));
        await Assert.ThrowsAsync<EvidenceDependencyException>(() => repo.SaveAsync(row with { State = "AVAILABLE", Pages = [] }, default));
    }
    [Fact] public async Task JobWorkloadRecordsOcrEvenWhenExtractionIsUnsupported()
    {
        var ocr = new InMemoryDocumentOcrRepository(); var classes = new InMemoryDocumentClassificationRepository(); var recognizer = new Recognizer(Tsv("Unknown document")); var renderer = new Renderer(1);
        var classifier = new DocumentClassifier(classes, recognizer, renderer, TimeProvider.System, new(ocr, recognizer, renderer, TimeProvider.System));
        var processing = new InMemoryDocumentProcessingRepository(); var app = new DocumentProcessingApplication(processing, new Reader(), new FailedExtractor(), new ReferenceExecutionPolicy(), new CapturingTelemetry(), TimeProvider.System, classifier);
        var request = Vs02TestContext.Request(Vs02ContractNames.ProcessDocument, new ProcessDocument("processing-ocr", "document-version-001", "evidence-001", "customer-001", DateTimeOffset.UtcNow), "ocr-request", "ocr-key", Vs02TestContext.Security() with { Workload = new("job-management", "job-management:T001") });
        await app.ProcessDocumentAsync(request); Assert.Equal(ProcessingState.Failed, (await processing.GetAsync("processing-ocr", default))!.State);
        Assert.NotNull(await ocr.ReadOcrAsync("T001", "customer-001", "document-version-001", default)); Assert.Equal("UNCLASSIFIED", (await classes.ReadAsync("T001", "customer-001", "document-version-001", default))!.DocumentClass);
    }
    [Fact] public async Task ExactSourceReadIsNoStoreAndMissingResultNeverStartsWork()
    {
        await using var host = await Host.Start(); var missing = await host.Client.GetFromJsonAsync<DocumentOcrResult>(""); Assert.Equal("NOT_PROCESSED", missing!.State);
        await host.Repository.SaveAsync(Row(), default); var response = await host.Client.GetAsync(""); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString()); Assert.Equal("Private document text", (await response.Content.ReadFromJsonAsync<DocumentOcrResult>())!.Pages[0].Lines[0].Text);
    }
    [Theory][InlineData("Advisor")][InlineData("TenantAdmin")][InlineData("Deny")][InlineData("Revoked")]
    public async Task CurrentSelfAuthorityRequiredBeforeReturningContent(string mode)
    {
        await using var host = await Host.Start(); await host.Repository.SaveAsync(Row(), default);
        if (mode == "Revoked") host.Authority.DenyAfter = 2; else if (mode == "Deny") host.Authority.DenyAfter = 0; else host.Authority.Basis = mode;
        var response = await host.Client.GetAsync(""); Assert.Equal(mode == "TenantAdmin" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden, response.StatusCode); Assert.DoesNotContain("Private document text", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    [Fact] public async Task ChangedSourceOrMissingSourceCannotExposeStoredText()
    {
        await using var host = await Host.Start(); await host.Repository.SaveAsync(Row(), default); host.Reader.Content = Source() with { Reference = Source().Reference with { ContentSha256 = new('b', 64) } };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("")).StatusCode); host.Reader.Content = null; Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("")).StatusCode);
    }
    [Fact] public async Task ForeignCustomerTenantQueriesAndOriginFailClosed()
    {
        await using var host = await Host.Start(); Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("?customerId=foreign")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/local/v1/customers/foreign/documents/document-version-001/ocr")).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-Monergy-Tenant"); host.Client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T002"); Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("")).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", "https://foreign.example"); Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("")).StatusCode);
    }
    private sealed class FailedExtractor : IDocumentExtractor { public Task<IReadOnlyList<ValidatedSourceFact>> ExtractAsync(EvidenceContent content, string processing, CancellationToken ct) => throw new DocumentRecognitionException(DocumentRecognitionFailure.Unclassified); }
    private sealed class Renderer(int count) : IPdfPageSetRenderer
    {
        public Task<byte[]> RenderAsync(byte[] pdf, CancellationToken ct) => throw new InvalidOperationException("Generic OCR must request every page.");
        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(byte[] pdf, CancellationToken ct) => Task.FromResult<IReadOnlyList<byte[]>>(Enumerable.Range(0, count).Select(_ => new byte[] { 1 }).ToArray());
    }
    private sealed class Recognizer(params string[] output) : IImageTextRecognizer
    { public int Calls, FailAfter = int.MaxValue; public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) { if (Calls >= FailAfter) throw new OcrEngineUnavailableException(); return Task.FromResult(output[Calls++]); } }
    private sealed class Reader : IEvidenceContentReader
    { public EvidenceContent? Content = Source(); public Task<EvidenceContent?> ReadAsync(string version, string customer, TrustedSecurityContext security, string correlation, CancellationToken ct) => Task.FromResult(Content); }
    private sealed class Authority : HttpMessageHandler
    {
        public string Basis = "Self"; public int Calls, DenyAfter = int.MaxValue;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; var input = (await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken: ct))!; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new CustomerAccessDecision("decision-one", Calls > DenyAfter ? "Deny" : "Allow", "T001", "A100", input.CustomerId, input.CapabilityId, input.Purpose, "Granted", Basis, 1, 1, 1, 1, DateTimeOffset.UtcNow)) }; }
    }
    private sealed class Host(WebApplication app, HttpClient client, Authority authority, InMemoryDocumentOcrRepository repo, Reader reader) : IAsyncDisposable
    {
        public HttpClient Client => client; public Authority Authority => authority; public InMemoryDocumentOcrRepository Repository => repo; public Reader Reader => reader;
        public static async Task<Host> Start()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0"); var authority = new Authority(); var repo = new InMemoryDocumentOcrRepository(); var reader = new Reader();
            builder.Services.AddSingleton(new TenantAccessClient(new ConfigurationManager { ["Monergy:ExecutionZone"] = "CI_EPHEMERAL" }, new HttpClient(authority)));
            builder.Services.AddSingleton(new TenantBoundaryOptions { Service = "document-intelligence", CapabilityPrefix = "document-intelligence.", ProbeUrl = "http://127.0.0.1:5112/", CustomerTenants = new Dictionary<string, string> { ["customer-001"] = "T001", ["foreign"] = "T002" } });
            builder.Services.AddSingleton<IDocumentOcrRepository>(repo); builder.Services.AddSingleton<IEvidenceContentReader>(reader);
            var app = builder.Build(); app.MapDocumentOcr(); await app.StartAsync(); var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(url + "/local/v1/customers/customer-001/documents/document-version-001/ocr") }; client.DefaultRequestHeaders.Add("X-Monergy-Tenant", "T001"); client.DefaultRequestHeaders.Add("X-Monergy-Session", "session-one"); return new(app, client, authority, repo, reader);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
