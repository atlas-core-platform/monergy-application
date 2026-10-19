using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ReviewedLiabilityRecordTests
{
    private const string Customer = FinancialProfileTestContext.CustomerId;
    private static CreateLiabilityRecord Input(string source,int revision=1) => new(new string('a',32),
        new("September debt","Example lender",400000m,new(2026,9,30),source,revision));
    [Theory,InlineData("operation"),InlineData("upper-operation"),InlineData("name"),InlineData("creditor"),InlineData("long-creditor"),InlineData("control"),
        InlineData("outstandingAmount"),InlineData("precision"),InlineData("large"),InlineData("early"),InlineData("future"),InlineData("source"),InlineData("revision")]
    public void InvalidInputIsRejectedBeforeSourceOrStorageAccess(string field)
    {
        var input=Input("ff-source");var draft=input.Liability;
        input=field switch {
            "operation"=>input with {OperationId="invalid"}, "upper-operation"=>input with {OperationId=new string('A',32)},
            "name"=>input with {Liability=draft with {Name=" "}},"creditor"=>input with {Liability=draft with {Creditor=" "}},
            "long-creditor"=>input with {Liability=draft with {Creditor=new string('x',121)}},"control"=>input with {Liability=draft with {Creditor="a\nb"}},
            "outstandingAmount"=>input with {Liability=draft with {OutstandingAmount=-1}},"precision"=>input with {Liability=draft with {OutstandingAmount=1.001m}},
            "large"=>input with {Liability=draft with {OutstandingAmount=1000000000000000m}},
            "early"=>input with {Liability=draft with {BalanceAsOfDate=new(1899,1,1)}},
            "future"=>input with {Liability=draft with {BalanceAsOfDate=new(2099,1,1)}},"source"=>input with {Liability=draft with {SourceFinancialFactId="bad source"}},
            _=>input with {Liability=draft with {SourceRevision=0}}
        };
        Assert.False(LiabilityRecordsApplication.Valid(input,DateTimeOffset.UtcNow));Assert.False(LiabilityRecordsApplication.Valid(null,DateTimeOffset.UtcNow));
    }
    [Fact]
    public void CurrentLocalFinancialDateIsValidBeforeUtcMidnightButFutureDatesAreNot()
    {
        var now = new DateTimeOffset(2026,10,18,20,0,0,TimeSpan.Zero);
        var input = Input("ff-source");
        Assert.True(LiabilityRecordsApplication.Valid(input with { Liability = input.Liability with { BalanceAsOfDate = new(2026,10,19) } }, now));
        Assert.False(LiabilityRecordsApplication.Valid(input with { Liability = input.Liability with { BalanceAsOfDate = new(2026,10,20) } }, now));
    }
    [Fact]
    public async Task ExactHistoricalSourceAndOriginalOutstandingAmountsSurviveRecordCreationAndReplay()
    {
        var (financial, source) = await Seed(); var repository = new InMemoryLiabilityRecords();
        var app = new LiabilityRecordsApplication(financial, repository, TimeProvider.System);
        var newSource = new ValidatedSourceFact("source-liability-2", Customer, "LOAN", "Loan balance stated in source", 110000m, "USD", new(2026,2,1), "line 2", 1m, "evidence-v2", "document-v2", "fixture-extractor/1.0.0", "validation/1.0.0");
        var financialApp = new FinancialProfileApplication(financial, new CapturingTelemetry(), TimeProvider.System);
        await financialApp.NormalizeSourceFactsAsync(FinancialProfileTestContext.Request(Vs02ContractNames.NormalizeSourceFacts,
            new NormalizeSourceFacts("profile-"+Customer, Customer, "processing-2", [newSource], "normalize/1.0.0", DateTimeOffset.UtcNow), "new-source", "new-source"));
        var request = Input(source.FinancialFactId);
        var first = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        var replay = await app.CreateAsync("T001", Customer, "actor-001", request, default);
        Assert.Equal(first, replay); Assert.Equal(400000m, first.Liability.OutstandingAmount);
        Assert.Equal("USD", first.Currency); Assert.Equal("actor-001", first.RecordedBy);Assert.Equal("CUSTOMER_REVIEWED",first.VerificationStatus);
        Assert.Equal(source.FinancialProvenanceId, first.SourceProvenanceId);
        Assert.Equal("document-v1", first.DocumentVersionId); Assert.Equal("evidence-v1", first.EvidenceId);
        Assert.Equal(100000m, (await financial.GetFactAsync(source.FinancialFactId, 1, default))!.Value);
        Assert.Equal(110000m, (await financial.GetFactAsync(source.FinancialFactId, null, default))!.Value);
        Assert.Equal(first, await app.ReadAsync("T001", Customer, first.LiabilityRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
        Assert.Empty((await app.ListAsync("T002", Customer, default)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T002", Customer, first.LiabilityRecordId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.ReadAsync("T001", "other", first.LiabilityRecordId, default))).Status);
    }
    [Fact]
    public async Task ChangedOperationPayloadConflictsWithoutChangingTheCreationReceipt()
    {
        var (financial, source) = await Seed(); var app = new LiabilityRecordsApplication(financial, new InMemoryLiabilityRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId); var first = await app.CreateAsync("T001", Customer, "actor-001", input, default);
        var error = await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", input with { Liability = input.Liability with { OutstandingAmount = 390000 } }, default));
        Assert.Equal(409, error.Status); Assert.Equal(first, await app.ReadAsync("T001", Customer, first.LiabilityRecordId, default));
        Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task CompetingPayloadsWithOneOperationCreateOnlyOneImmutableRecord()
    {
        var (financial, source) = await Seed(); var app = new LiabilityRecordsApplication(financial, new InMemoryLiabilityRecords(), TimeProvider.System);
        var input = Input(source.FinancialFactId);
        async Task<bool> Attempt(decimal outstanding) { try { await app.CreateAsync("T001", Customer, "actor-001", input with { Liability = input.Liability with { OutstandingAmount = outstanding } }, default); return true; } catch (TenantBoundaryException e) when (e.Status == 409) { return false; } }
        var results = await Task.WhenAll(Task.Run(() => Attempt(1)), Task.Run(() => Attempt(2)));
        Assert.Equal(1, results.Count(x => x)); Assert.Single((await app.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("INCOME", Customer), InlineData("EXPENSE", Customer), InlineData("PROPERTY", Customer), InlineData("CREDIT_CARD_ACCOUNT", Customer), InlineData("LOAN", "other-customer")]
    public async Task WrongSourceTypeOrForeignSourceCannotCreateARecord(string type, string customer)
    {
        var (financial, source) = await Seed(type, customer); var repository = new InMemoryLiabilityRecords();
        var app = new LiabilityRecordsApplication(financial, repository, TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId), default))).Status);
        Assert.Empty((await repository.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task MissingSourceRevisionCreatesNothing()
    {
        var (financial, source) = await Seed(); var app = new LiabilityRecordsApplication(financial, new InMemoryLiabilityRecords(), TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<TenantBoundaryException>(() => app.CreateAsync("T001", Customer, "actor-001", Input(source.FinancialFactId, 2), default))).Status);
    }
    [Fact]
    public async Task ActualOwnerAndGatewayRoutesCreateReadbackAndReplayOneSourceLinkedRecord()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        var created = await client.PostAsJsonAsync("/product-api/v1/liability-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<ReviewedLiabilityRecord>(ContractJson.Options); Assert.NotNull(first);
        Assert.Equal(input.Liability, first.Liability); Assert.Equal("actor-001", first.RecordedBy);Assert.Equal("CUSTOMER_REVIEWED",first.VerificationStatus);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var replay = await client.PostAsJsonAsync("/product-api/v1/liability-records", input, ContractJson.Options); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first, await replay.Content.ReadFromJsonAsync<ReviewedLiabilityRecord>(ContractJson.Options));
        var list = await client.GetFromJsonAsync<ProductLiabilityRecordList>("/product-api/v1/liability-records", ContractJson.Options);
        Assert.Equal(first, Assert.Single(list!.Items)); Assert.False(list.HasMore);
    }
    [Theory, InlineData("missing", 401), InlineData("deny", 403), InlineData("advisor", 403), InlineData("unavailable", 503), InlineData("tenant",401), InlineData("update",403), InlineData("evidence",403), InlineData("read",403)]
    public async Task CurrentAuthorityFailureCreatesNothing(string failure, int status)
    {
        await using var h = await Harness.Create(); h.Authority.Failure = failure; using var client = h.Client(failure == "missing" ? null : failure == "tenant" ? "T002" : "T001");
        var response = await client.PostAsJsonAsync("/product-api/v1/liability-records", Input(h.Source.FinancialFactId), ContractJson.Options);
        Assert.Equal(status, (int)response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Theory, InlineData("verificationStatus"), InlineData("currency"), InlineData("customerId"), InlineData("recordedBy"), InlineData("duplicate")]
    public async Task ActualOwnerRejectsExtraAuthorityFieldsAndDuplicateJson(string field)
    {
        await using var h = await Harness.Create(); using var client = h.OwnerClient(); var json = JsonSerializer.Serialize(Input(h.Source.FinancialFactId), ContractJson.Options);
        json = field == "duplicate" ? json.Insert(1, "\"operationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",") : json.Insert(1, "\"" + field + "\":\"forged\",");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/local/v1/customers/" + Customer + "/liability-records", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty((await h.Records.ListAsync("T001", Customer, default)).Items);
    }
    [Fact]
    public async Task LostConfirmationAfterCommitReplaysTheOriginalRecordAfterAuthorityReturns()
    {
        await using var h = await Harness.Create(); using var client = h.Client(); var input = Input(h.Source.FinancialFactId);
        h.Authority.DenyAfterEvaluations = 6;
        var unconfirmed = await client.PostAsJsonAsync("/product-api/v1/liability-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden,unconfirmed.StatusCode);
        var stored=Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.DenyAfterEvaluations = 0;
        var confirmed=await client.PostAsJsonAsync("/product-api/v1/liability-records",input,ContractJson.Options);
        Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        Assert.Equal(stored,await confirmed.Content.ReadFromJsonAsync<ReviewedLiabilityRecord>(ContractJson.Options));
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task OwnerBindingRejectsForeignTenantEvenWhenPdpAllowsACollidingCustomerId()
    {
        await using var h = await Harness.Create();
        using(var original = h.Client())
            Assert.Equal(HttpStatusCode.OK,(await original.PostAsJsonAsync("/product-api/v1/liability-records",Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        var saved = Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
        h.Authority.AllowForeignTenant = true;
        using var foreign = h.OwnerClient("T002");
        var path = "/local/v1/customers/"+Customer+"/liability-records";
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(path+"/"+saved.LiabilityRecordId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync(path,Input(h.Source.FinancialFactId),ContractJson.Options)).StatusCode);
        Assert.Empty((await h.Records.ListAsync("T002",Customer,default)).Items);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory, InlineData("customerId"),InlineData("recordedBy"),InlineData("currency"),InlineData("operationId"),InlineData("source"),InlineData("outstandingAmount"),InlineData("verificationStatus"),InlineData("null"),InlineData("readback")]
    public async Task GatewayRejectsUnboundOrUnconfirmedOwnerReceiptsAfterARealWrite(string tamper)
    {
        await using var h=await Harness.Create(tamper); using var client=h.Client();
        var response=await client.PostAsJsonAsync("/product-api/v1/liability-records",Input(h.Source.FinancialFactId),ContractJson.Options);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Single((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Theory, InlineData("unknown"),InlineData("nested"),InlineData("duplicate"),InlineData("missing"),InlineData("null"),InlineData("date"),InlineData("large")]
    public async Task GatewayStrictInputRejectsAmbiguousBodiesBeforeMutation(string variation)
    {
        await using var h=await Harness.Create(); using var client=h.Client();
        var json=JsonNode.Parse(JsonSerializer.Serialize(Input(h.Source.FinancialFactId),ContractJson.Options))!.AsObject();
        switch(variation) {
            case "unknown":json["currency"]="INR";break;
            case "nested":json["liability"]!["customerId"]="foreign";break;
            case "missing":json.Remove("operationId");break;
            case "null":json["liability"]=null;break;
            case "date":json["liability"]!["balanceAsOfDate"]="invalid";break;
            case "large":json["liability"]!["name"]=new string('x',40000);break;
        }
        var body=json.ToJsonString();if(variation=="duplicate") body=body.Insert(1,"\"operationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",");
        var response=await client.PostAsync("/product-api/v1/liability-records",new StringContent(body,Encoding.UTF8,"application/json"));
        Assert.Equal(variation=="large"?413:400,(int)response.StatusCode);Assert.Empty((await h.Records.ListAsync("T001",Customer,default)).Items);
    }
    [Fact]
    public async Task ExplicitZeroValuesAreAllowedAndTrimmedReviewReplaysWithoutChangingSourceValue()
    {
        var (financial,source)=await Seed();var repository=new InMemoryLiabilityRecords();var app=new LiabilityRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Liability=input.Liability with {Name=" September debt ",Creditor=" Example lender ",OutstandingAmount=0}};
        var first=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal("September debt",first.Liability.Name);Assert.Equal("Example lender",first.Liability.Creditor);
        Assert.Equal(0,first.Liability.OutstandingAmount);
        Assert.Equal(first,await app.CreateAsync("T001",Customer,"actor-001",input with {Liability=first.Liability},default));
        Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Fact]
    public async Task ListsExposeTheBoundWithoutImplyingACompleteLiabilityPositionAndOperationScopesRemainIndependent()
    {
        var (financial,source)=await Seed();var repository=new InMemoryLiabilityRecords();var app=new LiabilityRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);
        var one=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        var other=await app.CreateAsync("T001",Customer,"actor-002",input,default);Assert.NotEqual(one.LiabilityRecordId,other.LiabilityRecordId);
        for(var i=0;i<199;i++) await app.CreateAsync("T001",Customer,"actor-001",input with {OperationId=Guid.NewGuid().ToString("N")},default);
        var list=await app.ListAsync("T001",Customer,default);Assert.Equal(200,list.Items.Count);Assert.True(list.HasMore);
    }
    private sealed class TamperHandler(string variation) : DelegatingHandler(new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false})
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);
            if(!response.IsSuccessStatusCode || (variation=="readback") != (request.Method==HttpMethod.Get)) return response;
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
            switch(variation) {
                case "customerId":json["customerId"]="foreign";break;
                case "recordedBy":json["recordedBy"]="foreign";break;
                case "currency":json["currency"]="INR";break;
                case "verificationStatus":json["verificationStatus"]="VERIFIED";break;
                case "operationId":json["operationId"]=new string('b',32);break;
                case "source":json["liability"]!["sourceRevision"]=99;break;
                case "null":json["liability"]=null;break;
                default:json["liability"]!["outstandingAmount"]=1;break;
            }
            response.Content.Dispose();response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");return response;
        }
    }
    [Fact]
    public async Task ExplicitReviewedBalanceAndCreditorDoNotRewriteSource()
    {
        var (financial,source)=await Seed();var app=new LiabilityRecordsApplication(financial,new InMemoryLiabilityRecords(),TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Liability=input.Liability with {OutstandingAmount=1234.56m,Creditor=" Revised creditor ",BalanceAsOfDate=new(2026,8,1)}};
        var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
        Assert.Equal(1234.56m,saved.Liability.OutstandingAmount);Assert.Equal("Revised creditor",saved.Liability.Creditor);
        Assert.Equal(new DateOnly(2026,8,1),saved.Liability.BalanceAsOfDate);
        Assert.Equal("CUSTOMER_REVIEWED",saved.VerificationStatus);Assert.Equal(100000m,(await financial.GetFactAsync(source.FinancialFactId,1,default))!.Value);
    }
    [Theory,InlineData("999999999999.99",true),InlineData("1000000000000",false)]
    public async Task OutstandingAmountBoundPreservesCentPrecisionAndRejectsOutOfRangeRecords(string outstandingAmount,bool allowed)
    {
        var (financial,source)=await Seed();var repository=new InMemoryLiabilityRecords();var app=new LiabilityRecordsApplication(financial,repository,TimeProvider.System);
        var input=Input(source.FinancialFactId);input=input with {Liability=input.Liability with {OutstandingAmount=decimal.Parse(outstandingAmount,System.Globalization.CultureInfo.InvariantCulture)}};
        if(allowed)
        {
            var saved=await app.CreateAsync("T001",Customer,"actor-001",input,default);
            Assert.Equal(input.Liability.OutstandingAmount,saved.Liability.OutstandingAmount);Assert.Equal(saved,await app.ReadAsync("T001",Customer,saved.LiabilityRecordId,default));
        }
        else
        {
            Assert.Equal(400,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>app.CreateAsync("T001",Customer,"actor-001",input,default))).Status);
            Assert.Empty((await repository.ListAsync("T001",Customer,default)).Items);
        }
    }
    private static async Task<(InMemoryFinancialProfileRepository Financial, AuthoritativeFinancialFact Source)> Seed(string type = "LOAN", string customer = Customer)
    {
        var repository = new InMemoryFinancialProfileRepository(); var app = new FinancialProfileApplication(repository, new CapturingTelemetry(), TimeProvider.System);
        var fact = new ValidatedSourceFact("source-liability", customer, type, "Loan balance stated in source", 100000m, "USD", new(2026,1,1), "line 1", 1m, "evidence-v1", "document-v1", "fixture-extractor/1.0.0", "validation/1.0.0");
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
        public bool AllowForeignTenant { get; set; }
        private int evaluations;
        public void ResetEvaluations() => evaluations = 0;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Failure == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath == "/v1/authorization/customer-evaluations")
            {
                var input = await request.Content!.ReadFromJsonAsync<CustomerAccessRequest>(cancellationToken:ct);
                var tenant = request.Headers.GetValues("X-Monergy-Tenant").Single();
                var denied = DenyAfterEvaluations > 0 && ++evaluations > DenyAfterEvaluations || Failure == "deny" || !AllowForeignTenant && tenant != "T001" || input!.CustomerId != Customer ||
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
    private sealed class Harness(WebApplication owner, WebApplication gateway, Authority authority, InMemoryLiabilityRecords records, AuthoritativeFinancialFact source) : IAsyncDisposable
    {
        public Authority Authority => authority; public InMemoryLiabilityRecords Records => records; public AuthoritativeFinancialFact Source => source;
        public static async Task<Harness> Create(string? tamper=null)
        {
            var (financial, source) = await Seed(); var records = new InMemoryLiabilityRecords(); var authority = new Authority();
            var values = new Dictionary<string,string?> { ["Monergy:ExecutionZone"]="CI_EPHEMERAL", ["Monergy:AccessIntegration:Enabled"]="true", ["Monergy:Product:Enabled"]="true",
                ["Monergy:AccessIntegration:CustomerIdentityUrl"]="http://127.0.0.1:5001/", ["Monergy:AccessIntegration:AccessManagementUrl"]="http://127.0.0.1:5002/",
                ["Monergy:AccessIntegration:CustomerContextToken"]=new('a',32), ["Monergy:AccessIntegration:CustomerIdentityToken"]=new('b',32), ["Monergy:AccessIntegration:MembershipToken"]=new('c',32) };
            var ob = WebApplication.CreateSlimBuilder(); ob.WebHost.UseUrls("http://127.0.0.1:0"); ob.Configuration.AddInMemoryCollection(values);
            ob.Services.AddSingleton(new TenantAccessClient(ob.Configuration, new HttpClient(authority,false)));
            ob.Services.AddSingleton(new TenantBoundaryOptions { Service="financial-profile", CapabilityPrefix="financial-profile.", CustomerTenants=new Dictionary<string,string> { [Customer]="T001" }, ProbeUrl="http://127.0.0.1:5199/" });
            ob.Services.AddSingleton(new LiabilityRecordsApplication(financial,records,TimeProvider.System)); var owner=ob.Build(); owner.MapLiabilityRecords(); await owner.StartAsync();
            values["Monergy:Product:FinancialProfileUrl"]=owner.Urls.Single()+"/";
            var gb=WebApplication.CreateSlimBuilder(); gb.WebHost.UseUrls("http://127.0.0.1:0"); gb.Configuration.AddInMemoryCollection(values);
            gb.Services.AddSingleton(new CustomerOwnerClient(gb.Configuration,new HttpClient(authority,false)));
            gb.Services.AddSingleton(new TenantAccessClient(gb.Configuration,new HttpClient(authority,false))); gb.AddProductLiabilityRecords();
            if(tamper is not null) gb.Services.AddSingleton(new ProductLiabilityRecordClient(gb.Configuration,new HttpClient(new TamperHandler(tamper))));
            var gateway=gb.Build(); gateway.MapProductLiabilityRecords(); await gateway.StartAsync(); return new(owner,gateway,authority,records,source);
        }
        public HttpClient Client(string? tenant="T001") => ClientFor(gateway,tenant);
        public HttpClient OwnerClient(string tenant="T001") => ClientFor(owner,tenant);
        private static HttpClient ClientFor(WebApplication app,string? tenant)
        {
            var client=new HttpClient { BaseAddress=new Uri(app.Urls.Single()) };
            if(tenant is not null) { client.DefaultRequestHeaders.Add("X-Monergy-Tenant",tenant);client.DefaultRequestHeaders.Add("X-Monergy-Session","session-001"); }
            return client;
        }
        public async ValueTask DisposeAsync() { await gateway.DisposeAsync();await owner.DisposeAsync();authority.Dispose(); }
    }
}
