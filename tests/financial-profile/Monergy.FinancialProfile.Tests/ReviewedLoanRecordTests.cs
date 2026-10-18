using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.UatGateway;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed class ReviewedLoanRecordTests
{
    private static readonly string[] Changes = ["", "name", "lender", "control", "type", "sanction", "zero-sanction", "precision", "outstanding", "emi", "rate", "rate-high", "rate-precision", "tenure", "tenure-high", "start", "end", "asof", "future", "source", "revision"];
    public static TheoryData<string> InvalidChanges => new(Changes);
    private const string Customer = FinancialProfileTestContext.CustomerId;
    private static CreateLoanRecord Input(string source, int revision = 1) => new("a".PadLeft(32, 'a'),
        new("Home loan", "Example lender", "HOME", 500000m, 400000m, 8.25m, 10000m, 60,
            new(2026, 1, 1), new(2030, 12, 31), new(2026, 10, 1), source, revision));
    [Theory, MemberData(nameof(InvalidChanges))]
    public void InvalidTermsAreRejectedBeforeSourceOrStorageAccess(string field)
    {
        var input = Input("ff-source"); var loan = input.Loan;
        input = field switch {
            "" => input with { OperationId = "invalid" },
            "name" => input with { Loan = loan with { Name = " " } },
            "lender" => input with { Loan = loan with { Lender = new string('x',121) } },
            "control" => input with { Loan = loan with { Name = "a\nb" } },
            "type" => input with { Loan = loan with { LoanType = "GUESS" } },
            "sanction" => input with { Loan = loan with { SanctionAmount = -1 } },
            "zero-sanction" => input with { Loan = loan with { SanctionAmount = 0 } },
            "precision" => input with { Loan = loan with { SanctionAmount = 1.001m } },
            "outstanding" => input with { Loan = loan with { Outstanding = -1 } },
            "emi" => input with { Loan = loan with { Emi = -1 } },
            "rate" => input with { Loan = loan with { InterestRatePercent = -1 } },
            "rate-high" => input with { Loan = loan with { InterestRatePercent = 101 } },
            "rate-precision" => input with { Loan = loan with { InterestRatePercent = 1.12345m } },
            "tenure" => input with { Loan = loan with { TenureMonths = 0 } },
            "tenure-high" => input with { Loan = loan with { TenureMonths = 1201 } },
            "start" => input with { Loan = loan with { StartDate = new(1899,1,1) } },
            "end" => input with { Loan = loan with { EndDate = new(2025,1,1) } },
            "asof" => input with { Loan = loan with { BalanceAsOfDate = new(2025,1,1) } },
            "future" => input with { Loan = loan with { BalanceAsOfDate = new(2099,1,1) } },
            "source" => input with { Loan = loan with { SourceFinancialFactId = "bad source" } },
            _ => input with { Loan = loan with { SourceRevision = 0 } }
        };
        Assert.False(LoanRecordsApplication.Valid(input, DateTimeOffset.UtcNow));
        Assert.False(LoanRecordsApplication.Valid(null, DateTimeOffset.UtcNow));
    }
    [Fact]
    public void CurrentLocalFinancialDateIsValidBeforeUtcMidnightButFutureDatesAreNot()
    {
        var now = new DateTimeOffset(2026,10,18,20,0,0,TimeSpan.Zero);
        var input = Input("ff-source");
        Assert.True(LoanRecordsApplication.Valid(input with { Loan = input.Loan with { BalanceAsOfDate = new(2026,10,19) } }, now));
        Assert.False(LoanRecordsApplication.Valid(input with { Loan = input.Loan with { BalanceAsOfDate = new(2026,10,20) } }, now));
    }
    [Fact]
    public async Task ExactHistoricalSourceAndOriginalAmountsSurviveRecordCreationAndReplay()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryLoanRecords();
        var app = new LoanRecordsApplication(financial, repository, TimeProvider.System);
        var newSource = new ValidatedSourceFact("source-loan-2", Customer, "LOAN", "Amount stated in source", 110000m, "USD", new(2026,2,1), "line 2", 1m, "evidence-v2", "document-v2", "fixture-extractor/1.0.0", "validation/1.0.0");
        var financialApp = new FinancialProfileApplication(financial, new CapturingTelemetry(), TimeProvider.System);
        await financialApp.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+Customer, Customer, "processing-2", [newSource], "normalize/1.0.0", DateTimeOffset.UtcNow), "new-source", "new-source"));
        var request = Input(source.FinancialFactId);
        var first = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        var replay = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        Assert.Equal(first, replay); Assert.Equal(400000m, first.Loan.Outstanding);
        Assert.Equal("USD", first.Currency); Assert.Equal("actor-001", first.RecordedBy);
        Assert.Equal(source.FinancialProvenanceId, first.SourceProvenanceId);
        Assert.Equal("document-v1", first.DocumentVersionId); Assert.Equal("evidence-v1", first.EvidenceId);
        Assert.Equal(100000m, (await financial.GetFactAsync(source.FinancialFactId, 1, default))!.Value);
        Assert.Equal(110000m, (await financial.GetFactAsync(source.FinancialFactId, null, default))!.Value);
        Assert.Equal(first, await app.ReadAsync("T001", Customer, first.LoanRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
        Assert.Empty((await app.ListAsync("T002", Customer, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T002", Customer, first.LoanRecordId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T001", "other", first.LoanRecordId, default))).Status);
    }
    [Fact]
    public async Task ChangedOperationPayloadConflictsWithoutChangingTheCreationReceipt()
    {
        var (financial, source) = await Seed(); var app = new LoanRecordsApplication(financial, new InMemoryLoanRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId); var first = await app.CreateAsync("T001", Customer, "actor-001", input, default);
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", input with { Loan = input.Loan with { Outstanding = 390000 } }, default));
        Assert.Equal(409, error.Status); Assert.Equal(first, await app.ReadAsync("T001", Customer, first.LoanRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task CompetingPayloadsWithOneOperationCreateOnlyOneImmutableRecord()
    {
        var (financial, source) = await Seed(); var app = new LoanRecordsApplication(financial, new InMemoryLoanRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId);
        async Task<bool> Attempt(decimal outstanding) { try { await app.CreateAsync("T001", Customer, "actor-001", input with { Loan = input.Loan with { Outstanding = outstanding } }, default); return true; } catch (TenantBoundaryException e) when (e.Status == 409) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Attempt(1)), Task.Run(() => Attempt(2)));
        Assert.Equal(1, results.Count(x => x)); Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("INCOME", Customer), InlineData("LOAN", "other-customer")]
    public async Task WrongCategoryOrForeignSourceCannotCreateARecord(string type, string customer)
    {
        var (financial, source) = await Seed(type, customer); var repository = new InMemoryLoanRecords();
        var app = new LoanRecordsApplication(financial, repository, TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default))).Status);
        Assert.Empty((await repository.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task MissingSourceRevisionCreatesNothing()
    {
        var (financial, source) = await Seed(); var app = new LoanRecordsApplication(financial, new InMemoryLoanRecords(), TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId, 2), default))).Status);
    }
    [Fact]
    public async Task ActualOwnerAndGatewayRoutesCreateReadbackAndReplayOneSourceLinkedRecord()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        var created = await client.PostAsJsonAsync("/product-api/v1/loan-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options); Assert.NotNull(first);
        Assert.Equal(input.Loan, first.Loan); Assert.Equal("actor-001", first.RecordedBy);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var replay = await client.PostAsJsonAsync("/product-api/v1/loan-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options));
        var list = await client.GetFromJsonAsync<ProductLoanRecordList>("/product-api/v1/loan-records", ContractJson.Options);
        Assert.Equal(first, Assert.Single(list!.Items)); Assert.False(list.HasMore);
    }
    [Theory, InlineData("missing", 401), InlineData("deny", 403), InlineData("advisor", 403), InlineData("unavailable", 503), InlineData("tenant",401), InlineData("update",403), InlineData("evidence",403), InlineData("read",403)]
    public async Task CurrentAuthorityFailureCreatesNothing(string failure, int status)
    {
        await using var h = await Harness.Create(); h.Authority.Failure = failure; using var client = h.Client(failure == "missing" ? null : failure == "tenant" ? "T002" : "T001");
        var response = await client.PostAsJsonAsync("/product-api/v1/loan-records", Input(h.Source.FinancialFactId), ContractJson.Options);
        Assert.Equal(status, (int)response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("currency"), InlineData("customerId"), InlineData("recordedBy"), InlineData("duplicate")]
    public async Task ActualOwnerRejectsExtraAuthorityFieldsAndDuplicateJson(string field)
    {
        await using var h = await Harness.Create(); using var client = h.OwnerClient(); var json = JsonSerializer.Serialize(Input(h.Source.FinancialFactId), ContractJson.Options);
        json = field == "duplicate" ? json.Insert(1, "\"operationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",") : json.Insert(1, "\"" + field + "\":\"forged\",");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/local/v1/customers/" + Customer + "/loan-records", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task LostConfirmationAfterCommitReplaysTheOriginalRecordAfterAuthorityReturns()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        h.Authority.DenyAfterEvaluations = 6;
        var unconfirmed = await client.PostAsJsonAsync("/product-api/v1/loan-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden,unconfirmed.StatusCode);
        var stored=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.DenyAfterEvaluations = 0;
        var confirmed=await client.PostAsJsonAsync("/product-api/v1/loan-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        Assert.Equal(stored,await confirmed.Content.ReadFromJsonAsync<ReviewedLoanRecord>(ContractJson.Options));
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    private static async Task<(InMemoryFinancialProfileRepository Financial, AuthoritativeFinancialFact Source)> Seed(string type = "LOAN", string customer = Customer)
    {
        var repository = new InMemoryFinancialProfileRepository(); var app = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var fact = new ValidatedSourceFact("source-loan", customer, type, "Amount stated in source", 100000m, "USD", new(2026,1,1), "line 1", 1m, "evidence-v1", "document-v1", "fixture-extractor/1.0.0", "validation/1.0.0");
        var normalized = await app.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+customer, customer, "processing", [fact], "normalize/1.0.0", DateTimeOffset.UtcNow), "request-seed", "key-seed", FinancialProfileTestContext.Security(customer)));
        var id = normalized.Data!.Facts.Single().FinancialFactId;
        var source = await app.GetFinancialFactAsync(FinancialProfileTestContext.Request(Vs02ContractNames.GetFinancialFact, new GetFinancialFact(id,customer,1), "read-source", security:FinancialProfileTestContext.Security(customer)));
        return (repository, source.Data!);
    }
    private sealed class Authority : HttpMessageHandler
    {
        public string Failure { get; set; } = "";
        public int DenyAfterEvaluations { get; set; }
        private int evaluations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath == "/v1/authorization/customer-evaluations")
            {
                var input = await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:ct);
                var tenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
                var denied = DenyAfterEvaluations > 0 && ++evaluations > DenyAfterEvaluations || Failure == "deny" || tenant != "T001" || input!.CustomerId != Customer ||
                    Failure == "update" && input.CapabilityId.EndsWith("update", StringComparison.Ordinal) ||
                    Failure == "evidence" && input.CapabilityId == "evidence.document.read" || Failure == "read" && input.CapabilityId == "financial-profile.profile.read";
                return Response(new CustomerAccessDecision("decision-001", denied ? "Deny" : "Allow", tenant, "actor-001", input!.CustomerId,
                    input.CapabilityId, input.Purpose, denied ? "Denied" : "Granted", Failure == "advisor" ? "Advisor" : "Self", 1,2,1,1,DateTimeOffset.UtcNow));
            }
            if (request.RequestUri.AbsolutePath == "/internal/v1/tenant-sessions/validate")
                return Response(new CustomerOwnerSession("T001","actor-001","session-001",2,DateTimeOffset.UtcNow.AddHours(1)));
            if (request.RequestUri.AbsolutePath == "/internal/v1/customer-owner/actor-001")
                return Response(new CustomerOwnerWorkspace("T001",Customer,"Customer",null,"actor-001",1,true));
            throw new InvalidOperationException("Unexpected authority route.");
        }
        private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class Harness(WebApplication owner, WebApplication gateway, Authority authority, InMemoryLoanRecords records, AuthoritativeFinancialFact source) : IAsyncDisposable
    {
        public Authority Authority => authority; public InMemoryLoanRecords Records => records; public AuthoritativeFinancialFact Source => source;
        public static async Task<Harness> Create()
        {
            var (financial, source) = await Seed(); var records = new InMemoryLoanRecords(); var authority = new Authority();
            var values = new Dictionary<string,string?> { ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true", ["Monergy:Product:Enabled"]="true",
                ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",
                ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32) };
            var ob = WebApplication.CreateSlimBuilder(); ob.WebHost.UseUrls("http://127.0.0.1:0"); ob.Configuration.AddInMemoryCollection(values);
            ob.Services.AddSingleton(new TenantAccessClient(ob.Configuration, new HttpClient(authority,false)));
            ob.Services.AddSingleton(new LoanRecordsApplication(financial,records,TimeProvider.System)); var owner=ob.Build(); owner.MapLoanRecords(); await owner.StartAsync();
            values["Monergy:Product:FinancialProfileUrl"]=owner.Urls.Single()+"/";
            var gb=WebApplication.CreateSlimBuilder(); gb.WebHost.UseUrls("http://127.0.0.1:0"); gb.Configuration.AddInMemoryCollection(values);
            gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));
            gb.Services.AddSingleton(new TenantAccessClient(gb.Configuration,new HttpClient(authority,false))); gb.AddProductLoanRecords();
            var gateway=gb.Build(); gateway.MapProductLoanRecords(); await gateway.StartAsync(); return new(owner,gateway,authority,records,source);
        }
        public HttpClient Client(string? tenant="T001") => ClientFor(gateway,tenant);
        public HttpClient OwnerClient() => ClientFor(owner,"T001");
        private static HttpClient ClientFor(WebApplication app,string? tenant)
        {
            var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
            if(tenant is not null) { client.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);client.DefaultRequestHeaders.Add("X-Monergy-Session","session-001"); }
            return client;
        }
        public async ValueTask DisposeAsync() { await gateway.DisposeAsync();await owner.DisposeAsync();authority.Dispose(); }
    }
}
