using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.AiIntelligence.Qa;
using Monergy.Services.IntegrationGateway.AiModel;
using Xunit;
namespace Monergy.IntegrationGateway.Tests;

public sealed class FinancialQaTests
{
    private static readonly string[] PrivateValues = ["fact-1", "provenance-1", "version-1", "private-secret", "T001"];
    private static readonly QaClaim Claim = new("source-0", "Salary source amount 100 USD.", "FinancialFact", "fact-1", 1, "provenance-1", "version-1");
    [Theory]
    [InlineData("{\"state\":\"SOURCES\",\"claimIds\":[\"invented\"]}")]
    [InlineData("{\"state\":\"SOURCES\",\"claimIds\":[\"source-0\"],\"answer\":\"You have 999999\"}")]
    [InlineData("{\"state\":\"SOURCES\",\"claimIds\":[\"source-0\",\"source-0\"]}")]
    [InlineData("{\"state\":\"INSUFFICIENT_DATA\",\"claimIds\":[\"source-0\"]}")]
    [InlineData("{\"state\":\"SOURCES\",\"state\":\"INSUFFICIENT_DATA\",\"claimIds\":[]}")]
    public void InventedClaimsProseDuplicatesAndContradictionsCannotBecomeAnswers(string json)
    {
        var exception = Record.Exception(() => QaApplication.Select(json, [Claim]));
        Assert.True(exception is TenantBoundaryException or JsonException);
    }
    [Fact]
    public void ModelCanOnlySelectExactCanonicalClaims()
    {
        var answer = QaApplication.Select("""{"state":"SOURCES","claimIds":["source-0"]}""", [Claim]);
        Assert.Same(Claim, Assert.Single(answer));
        Assert.Empty(QaApplication.Select("""{"state":"INSUFFICIENT_DATA","claimIds":[]}""", [Claim]));
    }
    [Fact]
    public async Task PersistedReplayDoesNotRepeatModelSpendAndIsTenantBound()
    {
        using var fixture = new Fixture(); var owners = new Owners(); var service = new QaApplication(owners, fixture.Store);
        var input = Input(); var result = await service.Ask(Http(), input);
        Assert.Equal("SOURCES", result.State); Assert.Equal(2, owners.Calls);
        Assert.True(NetWorthData.Same(result, await service.Ask(Http(), input))); Assert.Equal(2, owners.Calls);
        Assert.Equal(409, (await Assert.ThrowsAsync<TenantBoundaryException>(() => service.Ask(Http(), input with { Question = "changed" }))).Status);
        owners.Tenant = "T002"; Assert.Empty(await service.History(Http()));
        owners.Tenant = "T001"; Assert.Single(await service.History(Http()));
        Assert.DoesNotContain("Salary", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, "answers.enc"))), StringComparison.Ordinal);
        using var reopened = new QaStore(fixture.Config); Assert.Single(await new QaApplication(owners, reopened).History(Http()));
    }
    [Fact]
    public async Task PermissionAndProviderRevisionAreRequiredBeforeSpend()
    {
        using var fixture = new Fixture(); var owners = new Owners(); var service = new QaApplication(owners, fixture.Store);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => service.Ask(Http(), Input() with { AllowModelProcessing = false }));
        await Assert.ThrowsAsync<TenantBoundaryException>(() => service.Ask(Http(), Input() with { ConfigurationRevision = "stale" }));
        Assert.Equal(0, owners.Calls);
    }
    [Fact]
    public async Task RevocationDuringGenerationPreventsDisclosureAndRecordsAttempt()
    {
        using var fixture = new Fixture(); var owners = new Owners { RevokeAfterSelection = true }; var service = new QaApplication(owners, fixture.Store);
        var input = Input(); await Assert.ThrowsAsync<TenantBoundaryException>(() => service.Ask(Http(), input));
        owners.Denied = false; owners.RevokeAfterSelection = false;
        Assert.Equal("NOT_COMPLETED", (await service.Ask(Http(), input)).State); Assert.Equal(2, owners.Calls);
    }
    [Fact]
    public async Task UnsupportedAndMissingDataNeverInvokeAnswerGeneration()
    {
        using var fixture = new Fixture(); var owners = new Owners { Unsupported = true }; var service = new QaApplication(owners, fixture.Store);
        Assert.Equal("UNSUPPORTED", (await service.Ask(Http(), Input())).State); Assert.Equal(1, owners.Calls);
        owners.Unsupported = false; owners.Empty = true;
        Assert.Equal("INSUFFICIENT_DATA", (await service.Ask(Http(), Input())).State); Assert.Equal(2, owners.Calls);
    }
    [Fact]
    public async Task FailedAttemptsAreRateLimitedAndReplayable()
    {
        using var fixture = new Fixture(); var owners = new Owners { Fail = true }; var service = new QaApplication(owners, fixture.Store);
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<HttpRequestException>(() => service.Ask(Http(), Input()));
        Assert.Equal(429, (await Assert.ThrowsAsync<TenantBoundaryException>(() => service.Ask(Http(), Input()))).Status);
        Assert.Equal(5, owners.Calls);
    }
    [Fact]
    public async Task SourceRevocationAlsoBlocksHistory()
    {
        using var fixture = new Fixture(); var owners = new Owners(); var service = new QaApplication(owners, fixture.Store);
        await service.Ask(Http(), Input()); owners.SourceDenied = true;
        await Assert.ThrowsAsync<TenantBoundaryException>(() => service.History(Http()));
    }
    [Fact]
    public async Task ProviderReceivesOnlyQuestionAndCanonicalDescriptions()
    {
        using var fixture = new Fixture(); var calls = 0;
        using var adapter = new QaModelAdapter(fixture.Config, new HttpClient(new Handler(async request => {
            calls++; Assert.Equal("https://model.example/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("private-secret", request.Headers.Authorization?.Parameter);
            Assert.True(request.Options.TryGetValue(QaModelAdapter.Sensitive, out var sensitive) && sensitive);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("Salary source amount", body, StringComparison.Ordinal);
            foreach (var secret in PrivateValues) Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"model":"pinned-version","choices":[{"finish_reason":"stop","message":{"content":"{\"state\":\"SOURCES\",\"claimIds\":[\"source-0\"]}"}}]}""") };
        })));
        var settings = adapter.Configuration("T001"); Assert.True(settings.Available); Assert.False(adapter.Configuration("T002").Available);
        var result = await adapter.GenerateAsync("T001", new("select", "Show salary", settings.Revision, true, [Claim]), default);
        Assert.Equal("pinned-version", result.Model); Assert.Equal(1, calls);
        await Assert.ThrowsAsync<TenantBoundaryException>(() => adapter.GenerateAsync("T001", new("select", "Show salary", settings.Revision, false, [Claim]), default));
        Assert.Equal(1, calls);
        fixture.Config["Monergy:AiModel:Tenants:T001:Notice"] = "Changed approved notice";
        Assert.NotEqual(settings.Revision, adapter.Configuration("T001").Revision);
    }
    [Theory]
    [InlineData("http://model.example/api")]
    [InlineData("https://localhost/api")]
    [InlineData("https://user:secret@model.example/api")]
    [InlineData("https://model.example/api?key=secret")]
    public void InvalidProviderConfigurationStaysUnavailable(string endpoint)
    {
        using var fixture = new Fixture(); fixture.Config["Monergy:AiModel:Tenants:T001:Endpoint"] = endpoint;
        using var adapter = new QaModelAdapter(fixture.Config, new HttpClient(new Handler(_ => throw new InvalidOperationException())));
        Assert.False(adapter.Configuration("T001").Available);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievalRehydratesOwnersAndRejectsCrossCustomerFacts(bool forged)
    {
        using var fixture = new Fixture();
        foreach (var pair in new Dictionary<string,string> {
            ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true",
            ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5101/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5088/",
            ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32),
            ["Monergy:FinancialQa:SearchRetrievalUrl"]="http://127.0.0.1:5115/", ["Monergy:FinancialQa:FinancialProfileUrl"]="http://127.0.0.1:5113/", ["Monergy:FinancialQa:EvidenceUrl"]="http://127.0.0.1:5111/"
        }) fixture.Config[pair.Key]=pair.Value;
        using var identity = new CustomerOwnerClient(fixture.Config);
        using var access = new TenantAccessClient(fixture.Config);
        using var owner = new QaOwners(identity, access, fixture.Config, new HttpClient(new Handler(async request => {
            Assert.Equal("T001",Assert.Single(request.Headers.GetValues("X-Monergy-Tenant")));
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body=json.RootElement;
            var name=body.GetProperty("contractName").GetString(); object data = name switch {
                SearchContractNames.SearchFinancialData => new SearchResults("salary",SearchMatchMode.Lexical,
                    [new("search-1",SearchResultType.Financial,"fake title","999999 fabricated derived summary","Financial Profile Service","DERIVED_REBUILDABLE",1,
                        new("FinancialFact","fact-1","source-1","provenance-1",null))]),
                Vs02ContractNames.GetFinancialFact => new AuthoritativeFinancialFact("fact-1","profile",forged?"other":"customer","INCOME","Salary",100,"USD",new(2026,10,1),1,"provenance-1"),
                Vs02ContractNames.GetFinancialProvenance => new FinancialProvenance("provenance-1","fact-1","customer","evidence-1","version-1","source-1","1","1","1","actor","workload",DateTimeOffset.UtcNow,"correlation",null),
                Vs02ContractNames.GetEvidenceReference => new EvidenceReference("evidence-1","document-1","version-1","customer","reference://private","hash","text/plain"),
                _ => throw new InvalidOperationException()
            };
            return new(HttpStatusCode.OK) { Content=System.Net.Http.Json.JsonContent.Create(new {
                contractName=name, contractVersion=body.GetProperty("contractVersion").GetString(), requestId=body.GetProperty("requestId").GetString(),
                correlationId=body.GetProperty("correlationId").GetString(), outcome="Success",data,error=(object?)null },options:ContractJson.Options) };
        })));
        var http=Http();http.Request.Headers["X-Monergy-Tenant"]="T001";http.Request.Headers["X-Monergy-Session"]="session";
        var customer=await new Owners().Authorize(http);var ids=new List<string>();
        if(forged) await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.Retrieve(http,customer,"salary",ids));
        else { var claim=Assert.Single(await owner.Retrieve(http,customer,"salary",ids));Assert.Contains("100 USD",claim.Text,StringComparison.Ordinal);Assert.DoesNotContain("999999",claim.Text,StringComparison.Ordinal);Assert.Equal("version-1",claim.DocumentVersionId); }
    }
    private static DefaultHttpContext Http() => new();
    private static AskFinancialQuestion Input() => new(Guid.NewGuid().ToString("N"), "Show salary source records", new string('a', 64), true);
    private sealed class Owners : IQaOwners
    {
        public string Tenant { get; set; } = "T001";
        public int Calls { get; private set; }
        public bool Denied { get; set; }
        public bool SourceDenied { get; set; }
        public bool RevokeAfterSelection { get; set; }
        public bool Empty { get; set; }
        public bool Unsupported { get; set; }
        public bool Fail { get; set; }
        public Task<QaCustomer> Authorize(HttpContext http)
        {
            if (Denied) throw new TenantBoundaryException("ACCESS_DENIED", 403);
            return Task.FromResult(new QaCustomer(new(Tenant, "actor", "session", 1, DateTimeOffset.UtcNow.AddHours(1)),
                new(Tenant, "customer", "Name", null, "actor", 1, true),
                new("decision", "Allow", Tenant, "actor", "customer", "ai-intelligence.analysis.execute", "customer-advice", "Granted", "Self", 1, 1, 1, 0, DateTimeOffset.UtcNow)));
        }
        public Task<QaConfiguration> Configuration(HttpContext http) => Task.FromResult(new QaConfiguration(true, new string('a', 64), "Provider", "Approved notice"));
        public Task<QaModelResult> Generate(HttpContext http, QaModelRequest input)
        {
            Calls++; if (Fail) throw new HttpRequestException();
            if (input.Phase == "plan") return Task.FromResult(new QaModelResult("model-v1", JsonSerializer.Serialize(new { mode = Unsupported ? "unsupported" : "lookup", query = "salary" })));
            if (RevokeAfterSelection) Denied = true;
            return Task.FromResult(new QaModelResult("model-v1", """{"state":"SOURCES","claimIds":["source-0"]}"""));
        }
        public Task<IReadOnlyList<QaClaim>> Retrieve(HttpContext http, QaCustomer customer, string query, List<string> ids)
        { ids.Add("search-1"); return Task.FromResult<IReadOnlyList<QaClaim>>(Empty ? [] : [Claim]); }
        public Task Validate(HttpContext http, QaCustomer customer, IEnumerable<QaClaim> claims)
        { if (SourceDenied) throw new TenantBoundaryException("ACCESS_DENIED", 403); return Task.CompletedTask; }
    }
    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "monergy-qa-" + Guid.NewGuid().ToString("N"));
        public IConfigurationRoot Config { get; }
        public QaStore Store { get; }
        public Fixture()
        {
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Monergy:FinancialQa:StoreDirectory"] = Directory, ["Monergy:FinancialQa:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
                ["Monergy:AiModel:Tenants:T001:Enabled"] = "true", ["Monergy:AiModel:Tenants:T001:Endpoint"] = "https://model.example/v1/chat/completions",
                ["Monergy:AiModel:Tenants:T001:ApiKey"] = "private-secret", ["Monergy:AiModel:Tenants:T001:Model"] = "pinned-model",
                ["Monergy:AiModel:Tenants:T001:ProviderLabel"] = "Provider", ["Monergy:AiModel:Tenants:T001:Notice"] = "Approved notice"
            }).Build(); Store = new(Config);
        }
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
}
